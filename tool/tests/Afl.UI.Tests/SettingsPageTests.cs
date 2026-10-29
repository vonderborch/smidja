using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Mcp;
using Afl.Core.Runs;
using Afl.UI.DataContexts;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for the Settings page's Agents, MCP servers and Execution tabs.
/// </summary>
public class SettingsPageTests : AbstractUiTest
{
    /// <summary>
    ///     Asserts that no file in the scratch home holds a secret value in the clear.
    /// </summary>
    /// <param name="value">The secret value.</param>
    private void AssertNotInJson(string value)
    {
        foreach (var file in Directory.EnumerateFiles(this.Paths.Root, "*.json", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(value, File.ReadAllText(file));
        }
    }

    private static McpToolInfo Tool(string name, string schema)
    {
        return new McpToolInfo(name, $"The {name} tool.\nMore detail.", JsonDocument.Parse(schema).RootElement.Clone());
    }

    [AvaloniaFact]
    public void AgentForm_FollowsTheKindsFieldsAndRoundTripsThroughJson()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        dc.NewAgent();
        var form = dc.AgentForm!;
        Assert.Equal(AgentConstants.KindCli, form.Kind);
        Assert.Contains(form.Fields, f => f.Name == "command" && f.Editor == "text" && f.Label == "Command *");

        form.Fields.Single(f => f.Name == "command").Text = "/bin/echo";
        var capabilities = form.Fields.Single(f => f.Name == "capabilities");
        Assert.True(capabilities.UsesItems);
        capabilities.Items.Single(i => i.Value == "review").IsSelected = true;

        // Another kind shows its own fields; switching back keeps what was typed.
        form.Kind = AgentConstants.KindMcp;
        Assert.Contains(form.Fields, f => f.Name == "server" && f.Editor == "server");
        Assert.Contains(form.Fields, f => f.Name == "tool" && f.Editor == "tool");
        Assert.True(form.HasToolFields);
        Assert.DoesNotContain(form.Fields, f => f.Name == "command");
        form.Kind = AgentConstants.KindCli;
        Assert.Equal("/bin/echo", form.Fields.Single(f => f.Name == "command").Text);
        Assert.True(form.Fields.Single(f => f.Name == "capabilities").Items.Single(i => i.Value == "review").IsSelected);

        // The JSON view shows the same agent, and edits there come back to the form.
        dc.IsAgentJson = true;
        Assert.True(form.IsJson);
        Assert.Contains("\"review\"", form.Json);
        form.Json = form.Json.Replace("/bin/echo", "/bin/cat");
        dc.IsAgentJson = false;
        Assert.Equal("/bin/cat", form.Fields.Single(f => f.Name == "command").Text);

        // JSON that doesn't parse keeps the JSON view open and says why.
        dc.IsAgentJson = true;
        form.Json = "{ nope";
        dc.IsAgentJson = false;
        Assert.True(dc.IsAgentJson);
        Assert.Contains("isn't valid", dc.ErrorMessage);
        form.Json = """{ "id": "my-agent", "name": "My agent", "kind": "cli", "command": "/bin/cat", "capabilities": ["review"] }""";
        dc.IsAgentJson = false;

        Assert.True(dc.SaveAgent());
        Assert.Equal(["review"], shell.Agents.GetRequired("my-agent").Capabilities!);
        Assert.Contains("review", dc.AgentList.Single(a => a.Id == "my-agent").Capabilities);
    }

    [AvaloniaFact]
    public void BuiltInAgents_AreShownReadOnly()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        Assert.Equal("claude", dc.SelectedAgent?.Id);
        Assert.True(dc.AgentForm!.IsReadOnly);
        Assert.All(dc.AgentForm.Fields, f => Assert.True(f.IsReadOnly));
        Assert.False(dc.CanEditAgent);
    }

    [AvaloniaFact]
    public void Servers_KeepSecretsOutOfTheJsonAndToolsBecomeAgents()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        dc.NewServer();
        dc.ServerId = "tools";
        dc.ServerName = "Tools";
        dc.ServerCommand = "/bin/true";
        dc.ServerArgs = "--stdio\n";
        dc.ServerEnv = "MODE=fast\nAPI_KEY={{secret.API_KEY}}";
        dc.NewSecretName = "API_KEY";
        dc.AddSecret();
        Assert.Equal("not set", dc.ServerSecrets.Single().Status);
        dc.ServerSecrets.Single().Value = "s3cret-value";

        Assert.True(dc.SaveServer());
        var saved = shell.Servers.GetRequired("tools");
        Assert.Equal(["--stdio"], saved.Args!);
        Assert.True(shell.Servers.HasSecret("tools", "API_KEY"));
        Assert.Equal("set", dc.ServerSecrets.Single().Status);
        Assert.Null(dc.ServerSecrets.Single().Value);
        this.AssertNotInJson("s3cret-value");

        // An inline value that looks secret moves to the secure store too.
        dc.ServerEnv = "MODE=fast\nAPI_KEY={{secret.API_KEY}}\nOTHER_TOKEN=inline-token-value";
        Assert.True(dc.SaveServer());
        Assert.True(shell.Servers.HasSecret("tools", "OTHER_TOKEN"));
        Assert.Contains(dc.ServerSecrets, s => s.Name == "OTHER_TOKEN" && s.IsSet);
        this.AssertNotInJson("inline-token-value");

        dc.ServerEnv = "not a pair";
        Assert.False(dc.SaveServer());
        Assert.Contains("NAME=value", dc.ErrorMessage);

        // A tool picked from the list becomes a draft mcp agent, its arguments guessed from the schema.
        dc.ServerTools.Add(Tool("ask", """{ "type": "object", "properties": { "prompt": {}, "cwd": {}, "depth": {} } }"""));
        dc.ServerTools.Add(Tool("reply", """{ "type": "object", "properties": { "threadId": {}, "prompt": {} } }"""));
        dc.SelectedTool = dc.ServerTools[0];
        Assert.True(dc.CreateAgentFromTool());
        var agent = dc.AgentForm!.ToAgent();
        Assert.Equal(("tools-ask", AgentConstants.KindMcp, "tools", "ask"), (agent.Id, agent.Kind, agent.Server, agent.Tool));
        Assert.Equal("The ask tool.", agent.Description);
        Assert.Equal("{{prompt}}", agent.Arguments!["prompt"]!.GetValue<string>());
        Assert.Equal("{{cwd}}", agent.Arguments["cwd"]!.GetValue<string>());
        Assert.False(agent.Arguments.ContainsKey("depth"));
        Assert.Equal("reply", agent.ResumeTool);
        Assert.Equal("{{session}}", agent.ResumeArguments!["threadId"]!.GetValue<string>());
        Assert.Null(dc.SelectedAgent);
        Assert.Contains("Drafted tools-ask", dc.AgentStatus);
        Assert.True(dc.SaveAgent());
        Assert.Equal("tools", shell.Agents.GetRequired("tools-ask").Server);

        // A second draft from the same tool gets a free id.
        Assert.True(dc.CreateAgentFromTool());
        Assert.Equal("tools-ask-2", dc.AgentForm!.ToAgent().Id);

        dc.SelectedServer = dc.ServerList.Single(s => s.Id == "tools");
        Assert.True(dc.RemoveServer());
        Assert.Null(shell.Servers.Find("tools"));
        Assert.False(shell.Servers.HasSecret("tools", "API_KEY"));
    }

    [AvaloniaFact]
    public void Import_ListsOtherAppsServersAndMovesTheirSecrets()
    {
        var home = Path.Combine(this.Root, "import-home");
        Directory.CreateDirectory(Path.Combine(home, ".cursor"));
        File.WriteAllText(Path.Combine(home, ".cursor", "mcp.json"), """
            { "mcpServers": {
                "gh": { "command": "npx", "args": ["-y", "gh-mcp"], "env": { "GITHUB_TOKEN": "ghp_fixture_value" } },
                "docs": { "url": "https://docs.example.com/mcp" } } }
            """);
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell) { ImportHome = home };

        dc.FindImports();
        Assert.True(dc.IsImporting);
        Assert.Equal(["docs", "gh"], dc.ImportCandidates.Select(c => c.Value.Server.Id).Order());
        Assert.All(dc.ImportCandidates, c => Assert.True(c.IsSelected));
        Assert.All(dc.ImportCandidates, c => Assert.DoesNotContain("ghp_fixture_value", c.Label));
        dc.ImportCandidates.Single(c => c.Value.Server.Id == "docs").IsSelected = false;

        Assert.Equal(1, dc.ImportServers());
        Assert.False(dc.IsImporting);
        Assert.Equal("gh", dc.SelectedServer?.Id);
        Assert.Null(shell.Servers.Find("docs"));
        var gh = shell.Servers.GetRequired("gh");
        Assert.StartsWith("{{secret.", gh.Env!["GITHUB_TOKEN"]);
        Assert.Contains(dc.ServerSecrets, s => s.IsSet);
        this.AssertNotInJson("ghp_fixture_value");

        // Found again, a registered server is unticked and says it would be replaced.
        dc.FindImports();
        var again = dc.ImportCandidates.Single(c => c.Value.Server.Id == "gh");
        Assert.False(again.IsSelected);
        Assert.Contains("replaces", again.Label);
        dc.CancelImport();
        Assert.Empty(dc.ImportCandidates);
    }

    [AvaloniaFact]
    public void Execution_SavesDefaultsCapabilitiesAndPreference()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        Assert.Equal("claude", dc.DefaultAgent);
        Assert.Equal(AgentConstants.RunModeBackground, dc.DefaultRunMode?.Value);
        Assert.Contains(dc.Selectors, s => s.Value == AgentConstants.SelectorFirst);

        dc.DefaultAgent = "session";
        dc.DefaultRunMode = dc.RunModes.Single(m => m.Value == AgentConstants.RunModeTerminal);
        dc.DefaultSelector = dc.Selectors.Single(s => s.Value == AgentConstants.SelectorFirst);
        Assert.True(dc.SaveExecution());
        var settings = shell.Settings.Load();
        Assert.Equal(("session", AgentConstants.RunModeTerminal, AgentConstants.SelectorFirst),
            (settings.DefaultAgent, settings.DefaultRunMode, settings.DefaultSelector));

        dc.NewCapabilityId = "Security";
        dc.NewCapabilityDescription = "Reviews code for security problems.";
        Assert.True(dc.AddCapability());
        Assert.Equal("security", dc.CustomCapabilities.Single().Id);
        dc.NewCapabilityId = "review";
        Assert.False(dc.AddCapability());
        Assert.Contains("already", dc.ErrorMessage);

        // Custom capabilities are offered in the agent form.
        dc.NewAgent();
        Assert.Contains(dc.AgentForm!.Fields.Single(f => f.Name == "capabilities").Items, i => i.Value == "security");

        dc.SelectedCapability = dc.CustomCapabilities.Single();
        Assert.True(dc.RemoveCapability());
        Assert.Empty(shell.Settings.Load().Capabilities);

        Assert.Equal(["claude", "session"], dc.AgentPreference);
        dc.SelectedPreference = "session";
        Assert.True(dc.MovePreference(-1));
        Assert.False(dc.MovePreference(-1));
        Assert.Equal(["session", "claude"], shell.Settings.Load().AgentPreference);
        Assert.Equal(["session", "claude"], dc.AgentPreference);
    }

    [AvaloniaFact]
    public async Task Notifications_SaveTheTickedStatesAndNeverFireInTests()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        Assert.Equal(["done", "failed", "needs-you"], dc.NotifyStates.Where(s => s.IsSelected).Select(s => s.Value).Order());
        Assert.Equal("This window doesn't send notifications.", dc.NotifierText);

        dc.NotifyStates.Single(s => s.Value == "done").IsSelected = false;
        Assert.True(dc.SaveNotifications());
        Assert.Equal([RunState.NeedsYou, RunState.Failed], shell.Settings.Load().NotifyOn);
        Assert.Equal("Saved.", dc.NotificationNote);

        foreach (var state in dc.NotifyStates)
        {
            state.IsSelected = false;
        }

        Assert.True(dc.SaveNotifications());
        Assert.Empty(shell.Settings.Load().NotifyOn);
        Assert.Equal("Saved; notifications are off.", dc.NotificationNote);

        // Tests' engines have no notifier, so nothing reaches the desktop.
        Assert.False(await dc.SendTestNotificationAsync());
        Assert.Equal(dc.NotifierText, dc.ErrorMessage);
    }
}
