using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Mcp;
using Afl.Core.Settings;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for agent capabilities, availability and the fields each agent kind declares.
/// </summary>
public class CapabilityTests : AbstractAflTest
{
    private AgentStore Agents() => new(this.Paths, new McpServerStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests")));

    [Fact]
    public void Catalog_HasBuiltInsAndTheUsersOwn()
    {
        Assert.Contains(CapabilityCatalog.BuiltIns, c => c.Id == "edit-code");
        var settings = new SettingsStore(this.Paths);
        settings.Save(new AflSettings { Capabilities = [new CapabilityDefinition { Id = "rust", Description = "Knows Rust." }] });

        var catalog = CapabilityCatalog.For(settings);

        Assert.True(catalog.Contains("rust"));
        Assert.True(catalog.Contains("review"));
        Assert.Equal("Knows Rust.", catalog.Describe("rust"));
        Assert.Throws<AflException>(() => settings.Save(new AflSettings
        {
            Capabilities = [new CapabilityDefinition { Id = "review" }],
        }));
    }

    [Fact]
    public void CapabilitiesOf_AddsWhatTheKindImplies()
    {
        var agents = this.Agents();
        var claude = agents.CapabilitiesOf(agents.GetRequired("claude"));
        Assert.Contains("edit-code", claude);
        Assert.Contains(CapabilityCatalog.Interactive, claude);
        Assert.Contains(CapabilityCatalog.Resume, claude);
        Assert.Contains(CapabilityCatalog.Permissions, claude);

        var plain = agents.CapabilitiesOf(new AgentDefinition
        {
            Id = "plain", Kind = AgentConstants.KindCli, Command = "x", Capabilities = ["review"],
        });
        Assert.Equal(["review"], plain);

        var resumable = agents.CapabilitiesOf(new AgentDefinition
        {
            Id = "r", Kind = AgentConstants.KindCli, Command = "x", ResumeArgs = ["--resume", "{{session}}"],
            InteractiveArgs = ["{{prompt}}"],
        });
        Assert.Equal([CapabilityCatalog.Interactive, CapabilityCatalog.Resume], resumable);
    }

    [Fact]
    public void Validate_RejectsBadCapabilityIds()
    {
        var problems = AgentStore.Validate(new AgentDefinition
        {
            Id = "a", Kind = AgentConstants.KindCli, Command = "x", Capabilities = ["Edit Code"],
        });
        Assert.Contains(problems, p => p.Contains("'Edit Code'"));
    }

    [Fact]
    public void Presets_DeclareCapabilities()
    {
        Assert.All(AgentStore.Presets, p => Assert.NotEmpty(p.Capabilities ?? []));
        Assert.All(AgentStore.Presets.SelectMany(p => p.Capabilities!),
            c => Assert.Contains(CapabilityCatalog.BuiltIns, b => b.Id == c));
    }

    [Fact]
    public void IsAvailable_LooksForTheCommandOrServer()
    {
        var bin = Path.Combine(this.Root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mytool"), "#!/bin/sh\n");
        var agents = this.Agents();
        agents.SearchPath = bin;

        Assert.True(agents.IsAvailable(new AgentDefinition { Id = "a", Kind = AgentConstants.KindCli, Command = "mytool" }));
        Assert.False(agents.IsAvailable(new AgentDefinition { Id = "b", Kind = AgentConstants.KindCli, Command = "nothere" }));
        Assert.True(agents.IsAvailable(agents.GetRequired("session")));

        var mcp = new AgentDefinition { Id = "m", Kind = AgentConstants.KindMcp, Server = "srv", Tool = "t" };
        Assert.False(agents.IsAvailable(mcp));
        agents.Servers.Save(new McpServerDefinition { Id = "srv", Command = "mytool" });
        Assert.True(agents.IsAvailable(mcp));
        agents.Servers.Save(new McpServerDefinition { Id = "srv", Command = "gone" });
        Assert.False(agents.IsAvailable(mcp));
        agents.Servers.Save(new McpServerDefinition { Id = "srv", Transport = AgentConstants.TransportHttp, Url = "https://x.example/mcp" });
        Assert.True(agents.IsAvailable(mcp));
    }

    [Fact]
    public void Fields_CoverEachKindAndRoundTrip()
    {
        foreach (var kind in ExecutorRegistry.Instance.Keys)
        {
            var fields = AgentStore.FieldsOf(kind);
            Assert.Equal(["id", "name", "description", "model", "capabilities"], fields.Take(5).Select(f => f.Name));
            Assert.Equal(fields.Count, fields.Select(f => f.Name).Distinct().Count());
            foreach (var field in fields)
            {
                Assert.Equal(string.Empty, field.GetText(new AgentDefinition()));
            }
        }

        var cli = AgentStore.FieldsOf(AgentConstants.KindCli).ToDictionary(f => f.Name);
        Assert.Equal(["command", "args", "promptVia", "output", "outputPath", "sessionPath", "modelArgs", "resumeArgs",
            "interactiveArgs", "mcpArgs", "mcpServers", "env"], cli.Keys.Skip(5));
        Assert.True(cli["command"].Required);
        Assert.Equal(AgentConstants.PromptVias, cli["promptVia"].Options);

        var agent = new AgentDefinition { Kind = AgentConstants.KindCli };
        cli["id"].SetText(agent, "  my-tool ");
        cli["args"].SetText(agent, "exec\n\n  {{prompt}}  \n");
        cli["env"].SetText(agent, "A=1\nB = two=2");
        cli["capabilities"].SetText(agent, "review\nfast");
        cli["outputPath"].SetText(agent, "   ");
        Assert.Equal("my-tool", agent.Id);
        Assert.Equal(["exec", "{{prompt}}"], agent.Args);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = " two=2" }, agent.Env);
        Assert.Equal(["review", "fast"], agent.Capabilities);
        Assert.Null(agent.OutputPath);
        Assert.Equal("exec\n{{prompt}}", cli["args"].GetText(agent));
        Assert.Throws<FormatException>(() => cli["env"].SetText(agent, "nope"));
        cli["args"].SetText(agent, null);
        Assert.Null(agent.Args);

        var mcp = AgentStore.FieldsOf(AgentConstants.KindMcp).ToDictionary(f => f.Name);
        Assert.Equal(AgentFieldEditors.Server, mcp["server"].Editor);
        Assert.Equal("server", mcp["tool"].DependsOn);
        var mcpAgent = new AgentDefinition { Kind = AgentConstants.KindMcp };
        mcp["arguments"].SetText(mcpAgent, """{ "prompt": "{{prompt}}", "n": 2 }""");
        Assert.Equal("{{prompt}}", mcpAgent.Arguments!["prompt"]!.GetValue<string>());
        Assert.Equal(2, mcpAgent.Arguments["n"]!.GetValue<int>());
        Assert.Equal(mcpAgent.Arguments.Count, JsonNode.Parse(mcp["arguments"].GetText(mcpAgent))!.AsObject().Count);
        Assert.Throws<FormatException>(() => mcp["arguments"].SetText(mcpAgent, "[1"));

        Assert.Equal(["command", "args", "mcpServers"], AgentStore.FieldsOf(ClaudeCliExecutor.ExecutorName).Skip(5).Select(f => f.Name));
        Assert.Equal(5, AgentStore.FieldsOf(SessionExecutor.ExecutorName).Count);
    }
}
