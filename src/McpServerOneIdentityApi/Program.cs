using IdentityModel.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal class Program
{
    private const string ExeName    = "McpServerOneIdentityApi.exe";
    private const string ServerName = "oimApi";
    private const string ClientAuto       = "auto";
    private const string ClientDesktop    = "desktop";
    private const string ClientClaudeCode = "claude-code";
    private const string ClientBoth       = "both";
    private const string DefaultScope     = "openid";
    private const string LogPrefix        = "[MCP OIM API]";

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            return await CliCommand.RunAsync(args);
        }

        var baseUrl       = GetRequiredEnvironmentVariable("OIM_BASE_URL",       Console.Error);
        var tokenEndpoint = GetRequiredEnvironmentVariable("OIM_TOKEN_ENDPOINT", Console.Error);
        var clientId      = GetRequiredEnvironmentVariable("OIM_CLIENT_ID",      Console.Error);
        var clientSecret  = GetRequiredEnvironmentVariable("OIM_CLIENT_SECRET",  Console.Error);

        if (baseUrl is null || tokenEndpoint is null || clientId is null || clientSecret is null)
            return 2;

        var scope = Environment.GetEnvironmentVariable("OIM_SCOPE", EnvironmentVariableTarget.Process)
                    ?? DefaultScope;

        await RunMcpServerAsync(baseUrl, tokenEndpoint, clientId, clientSecret, scope);
        return 0;
    }

    // -------------------------------------------------------------------------
    // Server startup
    // -------------------------------------------------------------------------

    private static async Task RunMcpServerAsync(
        string baseUrl, string tokenEndpoint, string clientId, string clientSecret, string scope)
    {
        var credential = new OimClientCredential(tokenEndpoint, clientId, clientSecret, scope);

        LogInfo($"Starting MCP server for {SanitizeBaseUrl(baseUrl)}. PID: {Environment.ProcessId}. " +
                "Authentication will happen when an OIM tool is called.");

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<OimApiTool>();

        builder.Services.AddSingleton(_ =>
            new HttpClient(new OimAuthenticationHandler(credential))
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
            });

        var app = builder.Build();
        await app.RunAsync();
    }

    private static string? GetRequiredEnvironmentVariable(string name, TextWriter error)
    {
        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        error.WriteLine($"{LogPrefix} Missing required environment variable: {name}");
        return null;
    }

    private static void LogInfo(string message)
    {
        var line = $"{LogPrefix} {DateTimeOffset.Now:O} {message}";
        Console.Error.WriteLine(line);

        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "McpServerOneIdentityApi",
                "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "mcp-server-oimApi.log"),
                line + Environment.NewLine);
        }
        catch
        {
            // Logging must never interfere with MCP stdio.
        }
    }

    private static string SanitizeBaseUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Host}";
        return "(invalid url)";
    }

    // -------------------------------------------------------------------------
    // Auth: OIM access token
    // -------------------------------------------------------------------------

    private readonly record struct OimAccessToken(string Token, DateTimeOffset ExpiresOn);

    private sealed class OimAuthenticationHandler : DelegatingHandler
    {
        private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);
        private readonly OimClientCredential _credential;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private OimAccessToken? _cachedToken;

        public OimAuthenticationHandler(OimClientCredential credential)
            : base(new HttpClientHandler())
        {
            _credential = credential;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            return await base.SendAsync(request, cancellationToken);
        }

        private async Task<OimAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (_cachedToken is { } current && IsUsable(current))
                return current;

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedToken is { } locked && IsUsable(locked))
                    return locked;

                LogInfo("Requesting One Identity Manager access token.");
                _cachedToken = await _credential.GetTokenAsync(cancellationToken);
                LogInfo($"Access token acquired. Expires: {_cachedToken.Value.ExpiresOn:O}");
                return _cachedToken.Value;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private static bool IsUsable(OimAccessToken token) =>
            token.ExpiresOn > DateTimeOffset.UtcNow.Add(TokenRefreshBuffer);
    }

    internal sealed class OimClientCredential
    {
        private readonly string _tokenEndpoint;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _scope;

        public OimClientCredential(
            string tokenEndpoint, string clientId, string clientSecret, string scope)
        {
            _tokenEndpoint = tokenEndpoint;
            _clientId      = clientId;
            _clientSecret  = clientSecret;
            _scope         = scope;
        }

        public async ValueTask<OimAccessToken> GetTokenAsync(CancellationToken cancellationToken)
        {
            using var http = new HttpClient();
            var response = await http.RequestClientCredentialsTokenAsync(
                new ClientCredentialsTokenRequest
                {
                    Address      = _tokenEndpoint,
                    ClientId     = _clientId,
                    ClientSecret = _clientSecret,
                    Scope        = _scope
                }, cancellationToken);

            if (response.IsError || string.IsNullOrWhiteSpace(response.AccessToken))
            {
                // Sanitize: only surface the OAuth error code, never secrets or full stack traces.
                var safeError = Regex.Replace(
                    response.Error ?? response.ErrorDescription ?? "unknown",
                    @"[^A-Za-z0-9_.: -]", string.Empty);
                throw new OimAuthException(
                    $"Token request failed. Error: {safeError}. " +
                    "Verify OIM_TOKEN_ENDPOINT, OIM_CLIENT_ID, OIM_CLIENT_SECRET, and OIM_SCOPE. " +
                    "Run --test-auth locally to diagnose.");
            }

            var expiresOn = DateTimeOffset.UtcNow.AddSeconds(
                response.ExpiresIn > 0 ? response.ExpiresIn : 3600);

            return new OimAccessToken(response.AccessToken, expiresOn);
        }
    }

    private sealed class OimAuthException(string message) : Exception(message);

    // -------------------------------------------------------------------------
    // CLI
    // -------------------------------------------------------------------------

    private sealed class CliCommand
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static async Task<int> RunAsync(string[] args)
        {
            var options = CliOptions.Parse(args);

            if (options.HasFlag("help") || options.HasFlag("?"))
            {
                WriteHelp(Console.Out);
                return 0;
            }

            if (options.HasFlag("install"))   return Install(options);
            if (options.HasFlag("uninstall")) return Uninstall(options);
            if (options.HasFlag("test-auth")) return await TestAuthAsync(options);

            Console.Error.WriteLine("Unknown command. Use --help.");
            return 2;
        }

        private static int Install(CliOptions options)
        {
            var baseUrl       = options.GetRequired("oim-base-url",    Console.Error);
            var tokenEndpoint = options.GetRequired("token-endpoint",  Console.Error);
            var clientId      = options.GetRequired("client-id",       Console.Error);
            var clientSecret  = options.GetRequired("client-secret",   Console.Error);

            if (baseUrl is null || tokenEndpoint is null || clientId is null || clientSecret is null)
                return 2;

            if (!ValidateClientOption(options))
                return 2;

            var scope = options.Get("scope", DefaultScope);

            var installRoot = ResolveInstallRoot(options, Console.Error);
            if (installRoot is null) return 2;

            Directory.CreateDirectory(installRoot);

            var sourceExe = GetCurrentExecutablePath();
            var targetExe = Path.Combine(installRoot, ExeName);

            if (!Path.GetFullPath(sourceExe).Equals(Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceExe, targetExe, overwrite: true);

            File.WriteAllText(Path.Combine(installRoot, "version.txt"), GetVersion());

            var configuredClients = ConfigureMcpClients(
                options, targetExe, baseUrl, tokenEndpoint, clientId, clientSecret, scope);

            Console.WriteLine($"Installed {ExeName} to {installRoot}");
            foreach (var msg in configuredClients)
                Console.WriteLine(msg);

            Console.WriteLine("Restart your MCP client before using the MCP server.");
            return 0;
        }

        private static int Uninstall(CliOptions options)
        {
            if (!ValidateClientOption(options))
                return 2;

            foreach (var msg in RemoveMcpClientConfigs(options))
                Console.WriteLine(msg);

            var installRoot = ResolveInstallRoot(options, Console.Error);
            if (installRoot is null) return 2;

            var targetExe  = Path.Combine(installRoot, ExeName);
            var currentExe = GetCurrentExecutablePath();

            if (Path.GetFullPath(currentExe).Equals(
                    Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"Close this process, then remove the install folder manually if needed: {installRoot}");
                return 0;
            }

            if (Directory.Exists(installRoot))
                Directory.Delete(installRoot, recursive: true);

            Console.WriteLine($"Removed {installRoot}");
            return 0;
        }

        private static async Task<int> TestAuthAsync(CliOptions options)
        {
            var tokenEndpoint = options.GetRequired("token-endpoint", Console.Error);
            var clientId      = options.GetRequired("client-id",      Console.Error);
            var clientSecret  = options.GetRequired("client-secret",  Console.Error);

            if (tokenEndpoint is null || clientId is null || clientSecret is null)
                return 2;

            var scope = options.Get("scope", DefaultScope);

            try
            {
                Console.WriteLine(
                    $"Testing One Identity Manager authentication against {tokenEndpoint}");
                var credential = new OimClientCredential(
                    tokenEndpoint, clientId, clientSecret, scope);
                var token = await credential.GetTokenAsync(CancellationToken.None);
                Console.WriteLine($"Authentication succeeded. Token expires: {token.ExpiresOn:O}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Authentication failed.");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static List<string> ConfigureMcpClients(
            CliOptions options, string command,
            string baseUrl, string tokenEndpoint,
            string clientId, string clientSecret, string scope)
        {
            var messages = new List<string>();
            var targets  = GetTargetClients(options);

            if (targets.Contains(ClientDesktop))
            {
                WriteClaudeDesktopConfig(
                    command, baseUrl, tokenEndpoint, clientId, clientSecret, scope);
                messages.Add(
                    $"Updated Claude Desktop MCP config: {GetClaudeDesktopConfigPath()}");
            }

            if (targets.Contains(ClientClaudeCode))
            {
                var claudeCodeExe = GetClaudeCodeExePath();
                if (claudeCodeExe is null)
                    messages.Add(
                        "Claude Code CLI was selected but was not found at " +
                        @"%USERPROFILE%\.local\bin\claude.exe.");
                else if (WriteClaudeCodeConfig(
                    claudeCodeExe, command, baseUrl, tokenEndpoint,
                    clientId, clientSecret, scope, out var error))
                    messages.Add(
                        "Updated Claude Code MCP config using 'claude mcp add --scope user'.");
                else
                    messages.Add($"Claude Code MCP config was not updated: {error}");
            }

            if (messages.Count == 0)
                messages.Add(
                    "No MCP client was detected. The server exe was installed, " +
                    "but client configuration was not changed.");

            return messages;
        }

        private static List<string> RemoveMcpClientConfigs(CliOptions options)
        {
            var messages = new List<string>();
            var targets  = GetTargetClients(options, includeExistingConfigs: true);

            if (targets.Contains(ClientDesktop))
            {
                RemoveClaudeDesktopConfigEntry();
                messages.Add($"Removed {ServerName} from Claude Desktop MCP config.");
            }

            if (targets.Contains(ClientClaudeCode))
            {
                var claudeCodeExe = GetClaudeCodeExePath();
                if (claudeCodeExe is null)
                    messages.Add(
                        "Claude Code CLI was selected but was not found at " +
                        @"%USERPROFILE%\.local\bin\claude.exe.");
                else if (RemoveClaudeCodeConfig(claudeCodeExe, out var error))
                    messages.Add($"Removed {ServerName} from Claude Code MCP config.");
                else
                    messages.Add($"Claude Code MCP config was not updated: {error}");
            }

            if (messages.Count == 0)
                messages.Add("No MCP client configuration was found to remove.");

            return messages;
        }

        private static bool ValidateClientOption(CliOptions options)
        {
            var value = options.Get("client", ClientAuto).Trim().ToLowerInvariant();
            if (value is ClientAuto or ClientDesktop or "claude-desktop"
                      or ClientClaudeCode or "cli" or "claude-cli" or "code" or ClientBoth)
                return true;

            Console.Error.WriteLine(
                "Unsupported --client value. Use auto, desktop, claude-code, or both.");
            return false;
        }

        private static HashSet<string> GetTargetClients(
            CliOptions options, bool includeExistingConfigs = false)
        {
            var value   = options.Get("client", ClientAuto).Trim().ToLowerInvariant();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (value is ClientDesktop or "claude-desktop")
            {
                targets.Add(ClientDesktop);
                return targets;
            }

            if (value is ClientClaudeCode or "cli" or "claude-cli" or "code")
            {
                targets.Add(ClientClaudeCode);
                return targets;
            }

            if (value == ClientBoth)
            {
                targets.Add(ClientDesktop);
                targets.Add(ClientClaudeCode);
                return targets;
            }

            // auto: detect what's installed
            if (IsClaudeCodeInstalled())
                targets.Add(ClientClaudeCode);

            var desktopConfigDir = Path.GetDirectoryName(GetClaudeDesktopConfigPath())!;
            if (Directory.Exists(desktopConfigDir) || File.Exists(GetClaudeDesktopConfigPath()))
                targets.Add(ClientDesktop);

            if (includeExistingConfigs && File.Exists(GetClaudeDesktopConfigPath()))
                targets.Add(ClientDesktop);

            if (targets.Count == 0)
                targets.Add(ClientDesktop);

            return targets;
        }

        private static void WriteClaudeDesktopConfig(
            string command, string baseUrl, string tokenEndpoint,
            string clientId, string clientSecret, string scope)
        {
            var configPath = GetClaudeDesktopConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            JsonObject config;
            if (File.Exists(configPath))
            {
                var existing = JsonNode.Parse(File.ReadAllText(configPath));
                config = existing as JsonObject ?? [];
            }
            else
            {
                config = [];
            }

            if (config["mcpServers"] is not JsonObject servers)
            {
                servers = [];
                config["mcpServers"] = servers;
            }

            servers[ServerName] = new JsonObject
            {
                ["command"] = command,
                ["args"]    = new JsonArray(),
                ["env"]     = new JsonObject
                {
                    ["OIM_BASE_URL"]       = baseUrl,
                    ["OIM_TOKEN_ENDPOINT"] = tokenEndpoint,
                    ["OIM_CLIENT_ID"]      = clientId,
                    ["OIM_CLIENT_SECRET"]  = clientSecret,
                    ["OIM_SCOPE"]          = scope
                }
            };

            File.WriteAllText(configPath, config.ToJsonString(JsonOptions));
        }

        private static bool WriteClaudeCodeConfig(
            string claudeCodeExe, string command,
            string baseUrl, string tokenEndpoint,
            string clientId, string clientSecret, string scope,
            out string error)
        {
            _ = RemoveClaudeCodeConfig(claudeCodeExe, out _);

            var serverConfig = new JsonObject
            {
                ["type"]    = "stdio",
                ["command"] = command,
                ["args"]    = new JsonArray(),
                ["env"]     = new JsonObject
                {
                    ["OIM_BASE_URL"]       = baseUrl,
                    ["OIM_TOKEN_ENDPOINT"] = tokenEndpoint,
                    ["OIM_CLIENT_ID"]      = clientId,
                    ["OIM_CLIENT_SECRET"]  = clientSecret,
                    ["OIM_SCOPE"]          = scope
                }
            };

            return RunProcess(
                claudeCodeExe,
                ["mcp", "add-json", "--scope", "user", ServerName, serverConfig.ToJsonString()],
                out error);
        }

        private static bool RemoveClaudeCodeConfig(string claudeCodeExe, out string error) =>
            RunProcess(claudeCodeExe,
                ["mcp", "remove", "--scope", "user", ServerName],
                out error);

        private static bool RunProcess(
            string fileName, IEnumerable<string> args, out string error)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.StartInfo.UseShellExecute        = false;
            process.StartInfo.RedirectStandardError  = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow         = true;

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return process.ExitCode == 0;
        }

        private static void RemoveClaudeDesktopConfigEntry()
        {
            var configPath = GetClaudeDesktopConfigPath();
            if (!File.Exists(configPath)) return;

            var existing = JsonNode.Parse(File.ReadAllText(configPath));
            if (existing is not JsonObject config
                || config["mcpServers"] is not JsonObject servers)
                return;

            servers.Remove(ServerName);
            File.WriteAllText(configPath, config.ToJsonString(JsonOptions));
        }

        private static string GetClaudeDesktopConfigPath() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude",
                "claude_desktop_config.json");

        private static bool IsClaudeCodeInstalled() => GetClaudeCodeExePath() is not null;

        private static string? GetClaudeCodeExePath()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "claude.exe");
            return File.Exists(path) ? path : null;
        }

        private static string GetDefaultInstallRoot(CliOptions options)
        {
            if (options.HasFlag("machine"))
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "McpServerOneIdentityApi");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "McpServerOneIdentityApi");
        }

        private static string? ResolveInstallRoot(CliOptions options, TextWriter error)
        {
            var requestedRoot = options.Get("install-root", GetDefaultInstallRoot(options));
            var fullRoot      = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(requestedRoot));
            var allowed = GetAllowedInstallRoots();

            if (allowed.Any(r => string.Equals(r, fullRoot, StringComparison.OrdinalIgnoreCase)))
                return fullRoot;

            error.WriteLine(
                "Unsupported install root. This executable only installs to the " +
                "approved per-user or machine-wide MCP folder.");
            error.WriteLine($"Per-user : {allowed.UserRoot}");
            error.WriteLine($"Machine  : {allowed.MachineRoot}");
            return null;
        }

        private static AllowedInstallRoots GetAllowedInstallRoots() =>
            new(
                Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "McpServerOneIdentityApi")),
                Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "McpServerOneIdentityApi")));

        private static string GetCurrentExecutablePath() =>
            Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, ExeName);

        private static string GetVersion() =>
            typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        private static void WriteHelp(TextWriter writer)
        {
            writer.WriteLine("McpServerOneIdentityApi");
            writer.WriteLine();
            writer.WriteLine("Default:");
            writer.WriteLine("  McpServerOneIdentityApi.exe");
            writer.WriteLine("    Starts the MCP stdio server. MCP clients use this mode.");
            writer.WriteLine();
            writer.WriteLine("Install:");
            writer.WriteLine("  McpServerOneIdentityApi.exe --install \\");
            writer.WriteLine("    --oim-base-url <https://server/AppServer> \\");
            writer.WriteLine("    --token-endpoint <https://server/rsts/oauth2/token> \\");
            writer.WriteLine("    --client-id <id> --client-secret <secret> \\");
            writer.WriteLine("    [--scope openid] [--client auto|desktop|claude-code|both]");
            writer.WriteLine();
            writer.WriteLine("Machine-wide install:");
            writer.WriteLine("  McpServerOneIdentityApi.exe --install --machine \\");
            writer.WriteLine("    --oim-base-url <url> --token-endpoint <url> \\");
            writer.WriteLine("    --client-id <id> --client-secret <secret>");
            writer.WriteLine();
            writer.WriteLine("Test authentication:");
            writer.WriteLine("  McpServerOneIdentityApi.exe --test-auth \\");
            writer.WriteLine("    --token-endpoint <url> --client-id <id> --client-secret <secret> \\");
            writer.WriteLine("    [--scope openid]");
            writer.WriteLine();
            writer.WriteLine("Uninstall:");
            writer.WriteLine("  McpServerOneIdentityApi.exe --uninstall");
            writer.WriteLine();
            writer.WriteLine("Install roots are fixed to approved folders:");
            writer.WriteLine(@"  User    %LOCALAPPDATA%\Programs\McpServerOneIdentityApi");
            writer.WriteLine(@"  Machine %ProgramFiles%\McpServerOneIdentityApi");
            writer.WriteLine();
            writer.WriteLine("Environment variables (set by --install, or manually for the MCP client):");
            writer.WriteLine("  OIM_BASE_URL        Base URL of the OIM Application Server or API Server");
            writer.WriteLine("                      e.g. https://oimserver/AppServer");
            writer.WriteLine("  OIM_TOKEN_ENDPOINT  RSTS OAuth2 token endpoint");
            writer.WriteLine("                      e.g. https://oimserver/rsts/oauth2/token");
            writer.WriteLine("  OIM_CLIENT_ID       OAuth2 client ID registered in RSTS");
            writer.WriteLine("  OIM_CLIENT_SECRET   OAuth2 client secret");
            writer.WriteLine("  OIM_SCOPE           Space-separated OAuth2 scopes (default: openid)");
        }

        private sealed record AllowedInstallRoots(string UserRoot, string MachineRoot)
        {
            public bool Any(Func<string, bool> predicate) =>
                predicate(UserRoot) || predicate(MachineRoot);
        }
    }

    // -------------------------------------------------------------------------
    // CLI option parser
    // -------------------------------------------------------------------------

    private sealed class CliOptions
    {
        private readonly Dictionary<string, string?> _values;

        private CliOptions(Dictionary<string, string?> values) => _values = values;

        public static CliOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;

                var keyValue = arg[2..].Split('=', 2);
                var key      = keyValue[0];
                string? value = null;

                if (keyValue.Length == 2)
                    value = keyValue[1];
                else if (i + 1 < args.Length
                         && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = args[++i];

                values[key] = value;
            }

            return new CliOptions(values);
        }

        public bool HasFlag(string name) => _values.ContainsKey(name);

        public string Get(string name, string defaultValue) =>
            _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : defaultValue;

        public string? GetRequired(string name, TextWriter error)
        {
            if (_values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

            error.WriteLine($"Missing required option: --{name}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // MCP Tools
    // -------------------------------------------------------------------------

    [McpServerToolType]
    public class OimApiTool
    {
        [McpServerTool(Name = "oim-api", Title = "GET against any OIM REST path")]
        [Description(
            "Read-only HTTP GET against any One Identity Manager AppServer/API Server REST path. " +
            "Use relative paths like '/base/Entity/Person'. " +
            "Common query params: where, OrderBy, PageSize, StartIndex, $select. " +
            "Returns raw JSON. For token-efficient summaries prefer the oim-list-* tools.")]
        public static async Task<string> CallOimApi(
            HttpClient client,
            [Description("Relative OIM REST path, e.g. '/base/Entity/Person'")] string path,
            [Description("Query string parameters as key/value pairs")] Dictionary<string, string>? queryParameters = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return "Error: path is required.";

                if (Uri.TryCreate(path, UriKind.Absolute, out _))
                    return "Error: path must be a relative OIM API path, e.g. '/base/Entity/Person'.";

                var requestPath = path.TrimStart('/');

                if (queryParameters?.Count > 0)
                {
                    var qs = string.Join("&", queryParameters
                        .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value is not null)
                        .Select(kvp =>
                            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
                    requestPath += $"?{qs}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
                request.Headers.Add("Accept", "application/json");

                using var response = await client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                return response.IsSuccessStatusCode
                    ? content
                    : SafeOimError(response, content);
            }
            catch (Exception ex)
            {
                return SafeExceptionMessage(ex);
            }
        }

        [McpServerTool(Name = "oim-whoami", Title = "Verify One Identity Manager connection")]
        [Description(
            "Verify authentication and connectivity to One Identity Manager by querying the " +
            "configured database. Use this first to confirm the MCP server can reach OIM and " +
            "that the OAuth2 client credentials are valid.")]
        public static async Task<string> GetServerInfo(HttpClient client)
        {
            try
            {
                const string url =
                    "base/Entity/DialogDatabase?PageSize=1&OrderBy=DisplayName" +
                    "&$select=UID_DialogDatabase,DisplayName,Revision,ProductVersionBuild";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "application/json");

                using var response = await client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                return response.IsSuccessStatusCode
                    ? content
                    : SafeOimError(response, content);
            }
            catch (Exception ex)
            {
                return SafeExceptionMessage(ex);
            }
        }

        [McpServerTool(Name = "oim-search-person", Title = "Search One Identity Manager persons")]
        [Description(
            "Search for persons (identities) in One Identity Manager by display name, " +
            "central account name, or primary email address. " +
            "Returns matching Person entities with key identity attributes.")]
        public static async Task<string> SearchPerson(
            HttpClient client,
            [Description(
                "Search term matched against DisplayName, CentralAccount, " +
                "and DefaultEmailAddress")] string searchTerm,
            [Description(
                "Maximum results to return (default 25, max 100)")] int pageSize = 25)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(searchTerm))
                    return "Error: searchTerm is required.";

                pageSize = Math.Clamp(pageSize, 1, 100);

                var safe  = SanitizeFilterValue(searchTerm);
                var where =
                    $"DisplayName like '%{safe}%' " +
                    $"OR CentralAccount like '%{safe}%' " +
                    $"OR DefaultEmailAddress like '%{safe}%'";

                const string select =
                    "UID_Person,DisplayName,CentralAccount,DefaultEmailAddress," +
                    "Department,UID_Department,XMarkedForDeletion";

                var url =
                    $"base/Entity/Person" +
                    $"?where={Uri.EscapeDataString(where)}" +
                    $"&OrderBy={Uri.EscapeDataString("DisplayName")}" +
                    $"&PageSize={pageSize}" +
                    $"&$select={Uri.EscapeDataString(select)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "application/json");

                using var response = await client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                return response.IsSuccessStatusCode
                    ? content
                    : SafeOimError(response, content);
            }
            catch (Exception ex)
            {
                return SafeExceptionMessage(ex);
            }
        }

        [McpServerTool(Name = "oim-get-person-accounts",
            Title = "Get target-system accounts for a One Identity Manager person")]
        [Description(
            "Retrieve all target-system accounts (Active Directory, SAP, LDAP, etc.) " +
            "linked to a specific person in One Identity Manager. " +
            "Requires the UID_Person value — use oim-search-person to find it first.")]
        public static async Task<string> GetPersonAccounts(
            HttpClient client,
            [Description(
                "UID_Person value identifying the person, " +
                "e.g. '{12345678-1234-1234-1234-123456789012}'")] string personUid,
            [Description(
                "Maximum results to return (default 50)")] int pageSize = 50)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(personUid))
                    return "Error: personUid is required.";

                pageSize = Math.Clamp(pageSize, 1, 200);

                var safe  = SanitizeFilterValue(personUid);
                var where = $"UID_Person = '{safe}'";

                const string select =
                    "UID_UNSAccount,AccountName,UID_Person," +
                    "UID_UNSRoot,DisplayName,XMarkedForDeletion";

                var url =
                    $"base/Entity/UNSAccount" +
                    $"?where={Uri.EscapeDataString(where)}" +
                    $"&OrderBy={Uri.EscapeDataString("AccountName")}" +
                    $"&PageSize={pageSize}" +
                    $"&$select={Uri.EscapeDataString(select)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "application/json");

                using var response = await client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                return response.IsSuccessStatusCode
                    ? content
                    : SafeOimError(response, content);
            }
            catch (Exception ex)
            {
                return SafeExceptionMessage(ex);
            }
        }

        [McpServerTool(Name = "oim-get-pending-approvals",
            Title = "Get pending IT Shop approval tasks in One Identity Manager")]
        [Description(
            "Retrieve open IT Shop workflow approval tasks waiting for a decision. " +
            "Returns PersonWantsOrg records with PWODecisionHistory and requester details. " +
            "Use oim-api with POST to approve or deny once you have the UID_PersonWantsOrg.")]
        public static async Task<string> GetPendingApprovals(
            HttpClient client,
            [Description(
                "Maximum results to return (default 25)")] int pageSize = 25)
        {
            try
            {
                pageSize = Math.Clamp(pageSize, 1, 100);

                const string where  = "OrderState = 'Assigned'";
                const string select =
                    "UID_PersonWantsOrg,DisplayOrg,PersonOrdered,OrderDate," +
                    "OrderState,UID_PersonOrdered,UID_PersonInserted";

                var url =
                    $"base/Entity/PersonWantsOrg" +
                    $"?where={Uri.EscapeDataString(where)}" +
                    $"&OrderBy={Uri.EscapeDataString("OrderDate")}" +
                    $"&PageSize={pageSize}" +
                    $"&$select={Uri.EscapeDataString(select)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "application/json");

                using var response = await client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                return response.IsSuccessStatusCode
                    ? content
                    : SafeOimError(response, content);
            }
            catch (Exception ex)
            {
                return SafeExceptionMessage(ex);
            }
        }

        // ---------------------------------------------------------------------
        // Schema discovery
        // ---------------------------------------------------------------------

        [McpServerTool(Name = "oim-list-entities", Title = "List OIM database tables")]
        [Description(
            "List OIM database tables (DialogTable). TSV: Name, DisplayName, IsCustom. " +
            "nameFilter is a SQL LIKE pattern matched against TableName or DisplaySingular.")]
        public static async Task<string> ListEntities(
            HttpClient client,
            [Description("Optional SQL LIKE pattern; use % as wildcard")] string nameFilter = "",
            [Description("Max rows (default 200, max 500)")] int pageSize = 200,
            [Description("Offset for pagination (default 0)")] int startIndex = 0)
        {
            try
            {
                pageSize   = Math.Clamp(pageSize, 1, 500);
                startIndex = Math.Max(0, startIndex);

                string? where = null;
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    var safe = SanitizeFilterValue(nameFilter);
                    where = $"TableName like '%{safe}%' OR DisplaySingular like '%{safe}%'";
                }

                return await GetEntitiesAsTsvAsync(
                    client, "DialogTable",
                    ["TableName", "DisplaySingular", "IsCustomTable"],
                    ["Name", "DisplayName", "IsCustom"],
                    where, "TableName", pageSize, startIndex);
            }
            catch (Exception ex) { return SafeExceptionMessage(ex); }
        }

        [McpServerTool(Name = "oim-describe-entity", Title = "List columns of an OIM table")]
        [Description(
            "List columns of an OIM table (DialogColumn). TSV: Column, Type, IsKey, IsFK, FKTableUID. " +
            "Resolve the FKTableUID with oim-list-entities if needed.")]
        public static async Task<string> DescribeEntity(
            HttpClient client,
            [Description("Table name, e.g. 'Person'")] string tableName,
            [Description("Max rows (default 200, max 500)")] int pageSize = 200,
            [Description("Offset for pagination (default 0)")] int startIndex = 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tableName))
                    return "Error: tableName is required.";

                pageSize   = Math.Clamp(pageSize, 1, 500);
                startIndex = Math.Max(0, startIndex);

                var safeName = SanitizeFilterValue(tableName);

                // Resolve UID_DialogTable first; OIM REST WHERE doesn't support subqueries.
                var lookupUrl =
                    "base/Entity/DialogTable" +
                    $"?where={Uri.EscapeDataString($"TableName = '{safeName}'")}" +
                    $"&PageSize=1" +
                    $"&$select={Uri.EscapeDataString("UID_DialogTable")}";

                using var lookupReq = new HttpRequestMessage(HttpMethod.Get, lookupUrl);
                lookupReq.Headers.Add("Accept", "application/json");
                using var lookupResp = await client.SendAsync(lookupReq);
                var lookupContent = await lookupResp.Content.ReadAsStringAsync();
                if (!lookupResp.IsSuccessStatusCode)
                    return SafeOimError(lookupResp, lookupContent);

                var tableUid = ExtractFirstFieldValue(lookupContent, "UID_DialogTable");
                if (string.IsNullOrEmpty(tableUid))
                    return $"Error: no table named '{tableName}' found.";

                var safeUid = SanitizeFilterValue(tableUid);
                var where   = $"UID_DialogTable = '{safeUid}'";

                return await GetEntitiesAsTsvAsync(
                    client, "DialogColumn",
                    ["ColumnName", "DataType", "IsPK", "IsForeignKey", "UID_DialogTableFK"],
                    ["Column", "Type", "IsKey", "IsFK", "FKTableUID"],
                    where, "ColumnName", pageSize, startIndex);
            }
            catch (Exception ex) { return SafeExceptionMessage(ex); }
        }

        // ---------------------------------------------------------------------
        // Processes, roles, IT Shop structure
        // ---------------------------------------------------------------------

        [McpServerTool(Name = "oim-list-itshop-structure", Title = "List IT Shop hierarchy nodes")]
        [Description(
            "List IT Shop hierarchy nodes (ITShopOrg: shops, shelves, customer nodes). " +
            "TSV: UID, Name, Type, ParentUID. Filter by parentUid to walk the tree.")]
        public static async Task<string> ListItshopStructure(
            HttpClient client,
            [Description("Optional SQL LIKE pattern on Ident_Org")] string nameFilter = "",
            [Description("Optional parent UID; lists direct children of that node")] string parentUid = "",
            [Description("Max rows (default 100, max 500)")] int pageSize = 100,
            [Description("Offset for pagination (default 0)")] int startIndex = 0)
        {
            try
            {
                pageSize   = Math.Clamp(pageSize, 1, 500);
                startIndex = Math.Max(0, startIndex);

                var clauses = new List<string>();
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    var safe = SanitizeFilterValue(nameFilter);
                    clauses.Add($"Ident_Org like '%{safe}%'");
                }
                if (!string.IsNullOrWhiteSpace(parentUid))
                {
                    var safe = SanitizeFilterValue(parentUid);
                    clauses.Add($"UID_ParentITShopOrg = '{safe}'");
                }
                string? where = clauses.Count > 0 ? string.Join(" AND ", clauses) : null;

                return await GetEntitiesAsTsvAsync(
                    client, "ITShopOrg",
                    ["UID_ITShopOrg", "Ident_Org", "ITShopInfo", "UID_ParentITShopOrg"],
                    ["UID", "Name", "Type", "ParentUID"],
                    where, "Ident_Org", pageSize, startIndex);
            }
            catch (Exception ex) { return SafeExceptionMessage(ex); }
        }

        [McpServerTool(Name = "oim-list-roles", Title = "List OIM application roles")]
        [Description(
            "List application roles (AERole). TSV: UID, Name, Description, ParentUID. " +
            "For system entitlement sets use oim-api on ESet directly.")]
        public static async Task<string> ListRoles(
            HttpClient client,
            [Description("Optional SQL LIKE pattern on Ident_AERole")] string nameFilter = "",
            [Description("Max rows (default 100, max 500)")] int pageSize = 100,
            [Description("Offset for pagination (default 0)")] int startIndex = 0)
        {
            try
            {
                pageSize   = Math.Clamp(pageSize, 1, 500);
                startIndex = Math.Max(0, startIndex);

                string? where = null;
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    var safe = SanitizeFilterValue(nameFilter);
                    where = $"Ident_AERole like '%{safe}%'";
                }

                return await GetEntitiesAsTsvAsync(
                    client, "AERole",
                    ["UID_AERole", "Ident_AERole", "Description", "UID_ParentAERole"],
                    ["UID", "Name", "Description", "ParentUID"],
                    where, "Ident_AERole", pageSize, startIndex);
            }
            catch (Exception ex) { return SafeExceptionMessage(ex); }
        }

        [McpServerTool(Name = "oim-list-approval-workflows", Title = "List approval workflow definitions")]
        [Description(
            "List approval workflow definitions (QERWorkingMethod). " +
            "TSV: UID, Name, Description. Use oim-api on QERWorkingStep filtered by UID_QERWorkingMethod " +
            "to inspect individual steps.")]
        public static async Task<string> ListApprovalWorkflows(
            HttpClient client,
            [Description("Optional SQL LIKE pattern on Ident_QERWorkingMethod")] string nameFilter = "",
            [Description("Max rows (default 100, max 500)")] int pageSize = 100,
            [Description("Offset for pagination (default 0)")] int startIndex = 0)
        {
            try
            {
                pageSize   = Math.Clamp(pageSize, 1, 500);
                startIndex = Math.Max(0, startIndex);

                string? where = null;
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    var safe = SanitizeFilterValue(nameFilter);
                    where = $"Ident_QERWorkingMethod like '%{safe}%'";
                }

                return await GetEntitiesAsTsvAsync(
                    client, "QERWorkingMethod",
                    ["UID_QERWorkingMethod", "Ident_QERWorkingMethod", "Description"],
                    ["UID", "Name", "Description"],
                    where, "Ident_QERWorkingMethod", pageSize, startIndex);
            }
            catch (Exception ex) { return SafeExceptionMessage(ex); }
        }

        // ---------------------------------------------------------------------
        // Tool helpers
        // ---------------------------------------------------------------------

        private static async Task<string> GetEntitiesAsTsvAsync(
            HttpClient client,
            string entity,
            string[] oimColumns,
            string[] tsvHeaders,
            string? where,
            string? orderBy,
            int pageSize,
            int startIndex)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(where))
                parts.Add($"where={Uri.EscapeDataString(where)}");
            if (!string.IsNullOrEmpty(orderBy))
                parts.Add($"OrderBy={Uri.EscapeDataString(orderBy)}");
            parts.Add($"PageSize={pageSize}");
            parts.Add($"StartIndex={startIndex}");
            parts.Add($"$select={Uri.EscapeDataString(string.Join(",", oimColumns))}");

            var url = $"base/Entity/{entity}?{string.Join("&", parts)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode
                ? ResponseToTsv(content, oimColumns, tsvHeaders, startIndex)
                : SafeOimError(response, content);
        }

        private static string ResponseToTsv(
            string content, string[] fields, string[] headers, int startIndex)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(content); }
            catch (JsonException) { return "Error: response was not valid JSON."; }
            if (root is null) return "Error: empty response.";

            var rows = FindRowArray(root);
            if (rows is null)
                return $"Error: no row array in response. Sample: {Truncate(content, 300)}";

            int? totalCount = null;
            if (root is JsonObject objRoot
                && objRoot["TotalCount"] is JsonValue tv
                && tv.TryGetValue<int>(out var tc))
                totalCount = tc;

            var sb = new StringBuilder();
            sb.AppendLine(string.Join('\t', headers));

            foreach (var row in rows)
            {
                if (row is null) continue;
                sb.AppendLine(string.Join('\t', fields.Select(f => ExtractFieldForTsv(row, f))));
            }

            int returned  = rows.Count;
            int nextIndex = startIndex + returned;
            if (totalCount.HasValue && returned > 0 && nextIndex < totalCount.Value)
                sb.Append($"# {returned} of {totalCount.Value} (next startIndex={nextIndex})");
            else
                sb.Append($"# {returned} rows");

            return sb.ToString();
        }

        private static JsonArray? FindRowArray(JsonNode root)
        {
            if (root is JsonArray a) return a;
            if (root is not JsonObject obj) return null;
            foreach (var key in new[] { "Entities", "Values", "value", "Items" })
                if (obj[key] is JsonArray arr) return arr;
            return null;
        }

        private static string ExtractFieldForTsv(JsonNode row, string field)
        {
            string? value = null;

            if (row is JsonObject rowObj)
            {
                if (TryReadStringFromValue(rowObj[field], out var direct))
                    value = direct;
                else if (rowObj["Columns"] is JsonObject cols
                         && TryReadStringFromValue(cols[field], out var byCol))
                    value = byCol;
                else if (rowObj["Values"] is JsonArray valArr)
                {
                    foreach (var item in valArr)
                    {
                        if (item is JsonObject pair
                            && pair["Name"] is JsonValue n
                            && string.Equals(n.ToString(), field, StringComparison.OrdinalIgnoreCase)
                            && TryReadStringFromValue(pair["Value"], out var byPair))
                        {
                            value = byPair;
                            break;
                        }
                    }
                }
            }

            if (value is null) return "";
            return value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static bool TryReadStringFromValue(JsonNode? node, out string? value)
        {
            value = null;
            if (node is null) return false;
            if (node is JsonValue v) { value = v.ToString(); return true; }
            if (node is JsonObject obj)
            {
                if (obj["Value"] is JsonValue iv)        { value = iv.ToString(); return true; }
                if (obj["DisplayValue"] is JsonValue dv) { value = dv.ToString(); return true; }
            }
            return false;
        }

        private static string? ExtractFirstFieldValue(string content, string field)
        {
            try
            {
                var root = JsonNode.Parse(content);
                if (root is null) return null;
                var rows = FindRowArray(root);
                if (rows is null || rows.Count == 0 || rows[0] is null) return null;
                var v = ExtractFieldForTsv(rows[0]!, field);
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch (JsonException) { return null; }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "...";

        private static string SanitizeFilterValue(string value)
        {
            // Strip characters that could form injection sequences in OIM WHERE syntax.
            // Preserve UIDs: letters, digits, hyphens, curly braces, dots, @, spaces.
            return Regex.Replace(value.Trim(), @"['\x00-\x1F\\;]", string.Empty);
        }

        private static string SafeOimError(HttpResponseMessage response, string content)
        {
            var details = TryReadOimError(content);
            var sb      = new StringBuilder();
            sb.Append("OIM request failed.");
            sb.Append(
                $" Status: {(int)response.StatusCode} " +
                $"{response.ReasonPhrase ?? response.StatusCode.ToString()}.");

            if (!string.IsNullOrWhiteSpace(details.Message))
                sb.Append($" Message: {details.Message}.");

            if (!string.IsNullOrWhiteSpace(details.ExceptionType))
                sb.Append($" Type: {details.ExceptionType}.");

            sb.Append(
                " Full OIM error content was withheld from the model. " +
                "Check the OIM Application Server logs for details.");

            return sb.ToString();
        }

        private static OimErrorDetails TryReadOimError(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new(null, null);

            try
            {
                using var doc  = JsonDocument.Parse(content);
                var root       = doc.RootElement;
                var message    = ReadJsonString(root, "Message")
                                 ?? ReadJsonString(root, "message")
                                 ?? ReadJsonString(root, "error_description");
                var exType     = ReadJsonString(root, "ExceptionType")
                                 ?? ReadJsonString(root, "error");
                return new(SanitizeIdentifier(message), SanitizeIdentifier(exType));
            }
            catch (JsonException)
            {
                return new(null, null);
            }
        }

        private static string? ReadJsonString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var prop)
            && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;

        private static string? SanitizeIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var safe = Regex.Replace(value.Trim(), @"[^A-Za-z0-9_.:@\- ]", string.Empty);
            return safe.Length > 120 ? safe[..120] : safe;
        }

        private sealed record OimErrorDetails(string? Message, string? ExceptionType);
    }

    // -------------------------------------------------------------------------
    // Shared exception helper
    // -------------------------------------------------------------------------

    private static string SafeExceptionMessage(Exception ex)
    {
        if (ex is OimAuthException)
            return $"OIM authentication failed. {ex.Message}";

        if (ex is HttpRequestException)
            return $"OIM HTTP request failed: {ex.GetType().Name}. " +
                   "Check OIM_BASE_URL and network connectivity. " +
                   "Detailed output was withheld from the model.";

        if (ex is TaskCanceledException or OperationCanceledException)
            return "OIM request timed out or was cancelled.";

        return $"MCP OIM server error: {ex.GetType().Name}. " +
               "Detailed exception output was withheld from the model.";
    }
}
