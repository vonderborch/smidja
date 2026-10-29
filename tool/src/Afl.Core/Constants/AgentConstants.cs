namespace Afl.Core.Constants;

/// <summary>
///     Agent kinds, options and the template variables agents' arguments can use.
/// </summary>
public static class AgentConstants
{
    /// <summary>
    ///     An agent launched as a command (any AI command-line tool).
    /// </summary>
    public const string KindCli = "cli";

    /// <summary>
    ///     An agent reached through a tool on an MCP server.
    /// </summary>
    public const string KindMcp = "mcp";

    /// <summary>
    ///     The prompt goes in the arguments, where <c>{{prompt}}</c> is, or last.
    /// </summary>
    public const string PromptViaArg = "arg";

    /// <summary>
    ///     The prompt is written to the command's standard input.
    /// </summary>
    public const string PromptViaStdin = "stdin";

    /// <summary>
    ///     The prompt is written to a file whose path goes in the arguments, where <c>{{promptFile}}</c> is, or last.
    /// </summary>
    public const string PromptViaFile = "file";

    /// <summary>
    ///     The output is the command's standard output as it is.
    /// </summary>
    public const string OutputText = "text";

    /// <summary>
    ///     The output is picked from the command's JSON (or JSON-lines) output with <c>outputPath</c>.
    /// </summary>
    public const string OutputJson = "json";

    /// <summary>
    ///     The MCP server is a command afl starts, talking over its standard streams.
    /// </summary>
    public const string TransportStdio = "stdio";

    /// <summary>
    ///     The MCP server is already running at a URL.
    /// </summary>
    public const string TransportHttp = "http";

    /// <summary>
    ///     The variable holding the rendered prompt.
    /// </summary>
    public const string PromptVariable = "prompt";

    /// <summary>
    ///     The variable holding the path of a file containing the prompt.
    /// </summary>
    public const string PromptFileVariable = "promptFile";

    /// <summary>
    ///     The variable holding the path of a file the agent writes its answer to; when an agent's arguments use it, the
    ///     output is read from that file.
    /// </summary>
    public const string OutputFileVariable = "outputFile";

    /// <summary>
    ///     The variable holding the working directory (the repo root).
    /// </summary>
    public const string CwdVariable = "cwd";

    /// <summary>
    ///     The variable holding the model.
    /// </summary>
    public const string ModelVariable = "model";

    /// <summary>
    ///     The variable holding the conversation id.
    /// </summary>
    public const string SessionVariable = "session";

    /// <summary>
    ///     Every variable an agent's arguments can use.
    /// </summary>
    public static readonly IReadOnlyList<string> Variables =
        [PromptVariable, PromptFileVariable, OutputFileVariable, CwdVariable, ModelVariable, SessionVariable, McpConfigFileVariable];

    /// <summary>
    ///     The ways a prompt can reach a command.
    /// </summary>
    public static readonly IReadOnlyList<string> PromptVias = [PromptViaArg, PromptViaStdin, PromptViaFile];

    /// <summary>
    ///     The ways a command's output can be read.
    /// </summary>
    public static readonly IReadOnlyList<string> Outputs = [OutputText, OutputJson];

    /// <summary>
    ///     The ways to reach an MCP server.
    /// </summary>
    public static readonly IReadOnlyList<string> Transports = [TransportStdio, TransportHttp];

    /// <summary>
    ///     The run mode that runs agents' commands in the background, capturing their output.
    /// </summary>
    public const string RunModeBackground = "background";

    /// <summary>
    ///     The run mode that runs agents' commands in a visible terminal window you can watch.
    /// </summary>
    public const string RunModeTerminal = "terminal";

    /// <summary>
    ///     The agent selector that takes the first qualifying agent in preference order.
    /// </summary>
    public const string SelectorFirst = "first";

    /// <summary>
    ///     The agent selector that asks the run's main agent to choose.
    /// </summary>
    public const string SelectorAi = "ai";

    /// <summary>
    ///     Who chose a step's agent: it was the step's preferred agent and it qualified.
    /// </summary>
    public const string ChosenByPreference = "preference";

    /// <summary>
    ///     Who chose a step's agent: a selector picked it by rule (such as the first in preference order).
    /// </summary>
    public const string ChosenBySelector = "selector";

    /// <summary>
    ///     Who chose a step's agent: the run's main agent.
    /// </summary>
    public const string ChosenByMainAgent = "main-agent";

    /// <summary>
    ///     Who chose a step's agent: a person.
    /// </summary>
    public const string ChosenByUser = "user";

    /// <summary>
    ///     The values of <c>AgentChoice.By</c> someone can give when picking an agent with <c>afl run use-agent</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> PickedBy = [ChosenByUser, ChosenByMainAgent];

    /// <summary>
    ///     The prefix of the template variables that stand for an MCP server's secrets, as in <c>{{secret.TOKEN}}</c>.
    /// </summary>
    public const string SecretVariablePrefix = "secret.";

    /// <summary>
    ///     The template variable for the MCP client configuration file given to an agent (<c>mcpArgs</c>).
    /// </summary>
    public const string McpConfigFileVariable = "mcpConfigFile";
}
