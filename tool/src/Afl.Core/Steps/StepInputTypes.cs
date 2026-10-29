using System.Globalization;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Registration;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     The kinds of control front-ends use to ask for an input. Each input type picks one, so a new type works in the
///     CLI, the UI and the plugin without them knowing about it.
/// </summary>
public enum StepInputEditor
{
    /// <summary>
    ///     A single line of text.
    /// </summary>
    Text,

    /// <summary>
    ///     Several lines of text.
    /// </summary>
    MultilineText,

    /// <summary>
    ///     Pick one of the options.
    /// </summary>
    Choice,

    /// <summary>
    ///     Pick any of the options.
    /// </summary>
    MultiChoice,

    /// <summary>
    ///     Yes or no.
    /// </summary>
    Toggle,
}

/// <summary>
///     Registers a step input type with the <see cref="StepInputTypeRegistry" />.
/// </summary>
/// <param name="type">The value of <see cref="StepInput.Type" /> it handles, e.g. <c>choice</c>.</param>
public class StepInputTypeRegistrationAttribute(string type) : AbstractRegistrationAttribute(type);

/// <summary>
///     The step input types afl knows about, keyed by <see cref="StepInput.Type" />. Also checks a set of answers
///     against a step's inputs, which every front-end does the same way.
/// </summary>
public class StepInputTypeRegistry() : TypeRegistry<AbstractStepInputType, StepInputTypeRegistrationAttribute>("input type")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static StepInputTypeRegistry Instance { get; } = new();

    /// <summary>
    ///     Creates the type for an input.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The input type.</returns>
    public AbstractStepInputType For(StepInput input)
    {
        return this.Create(input.Type);
    }

    /// <summary>
    ///     Checks raw answers against a step's inputs: fills in defaults, requires required answers, and puts each
    ///     answer in its type's canonical form.
    /// </summary>
    /// <param name="inputs">The step's inputs, if any.</param>
    /// <param name="raw">The answers given, by input id. Blank answers count as not given.</param>
    /// <returns>The answers to keep, by input id. Optional inputs left blank are left out.</returns>
    /// <exception cref="AflException">Answers are missing, unknown or invalid; the message lists every problem.</exception>
    public Dictionary<string, string> Resolve(IReadOnlyList<StepInput>? inputs, IReadOnlyDictionary<string, string>? raw)
    {
        inputs ??= [];
        raw ??= new Dictionary<string, string>();
        var errors = raw.Keys.Where(k => inputs.All(i => i.Id != k))
            .Select(k => $"'{k}' isn't an input of this step. Inputs: {Describe(inputs)}.")
            .ToList();
        var answers = new Dictionary<string, string>();
        foreach (var input in inputs)
        {
            try
            {
                if (this.For(input).Resolve(input, raw.GetValueOrDefault(input.Id)) is { } answer)
                {
                    answers[input.Id] = answer;
                }
            }
            catch (AflException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, string.Join(" ", errors));
        }

        return answers;
    }

    /// <summary>
    ///     Lists input ids for messages.
    /// </summary>
    /// <param name="inputs">The inputs.</param>
    /// <returns>The ids, or "none".</returns>
    private static string Describe(IReadOnlyList<StepInput> inputs)
    {
        return inputs.Count == 0 ? "none" : string.Join(", ", inputs.Select(i => i.Id));
    }
}

/// <summary>
///     Base for a kind of step input: how it is checked in a workflow, how it is asked (<see cref="Editor" />) and how
///     an answer is turned into the text later steps see. A new kind is one subclass with a
///     <see cref="StepInputTypeRegistrationAttribute" />.
/// </summary>
public abstract class AbstractStepInputType
{
    /// <summary>
    ///     Gets the control front-ends ask with.
    /// </summary>
    public abstract StepInputEditor Editor { get; }

    /// <summary>
    ///     Gets whether the input picks from <see cref="StepInput.Options" />.
    /// </summary>
    public virtual bool UsesOptions => false;

    /// <summary>
    ///     Checks an input definition for problems specific to this type. The base checks options are given only when
    ///     used, and that the default is a valid answer.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(StepInput input)
    {
        var options = input.Options ?? [];
        if (this.UsesOptions && options.Count == 0)
        {
            yield return $"input '{input.Id}' ({input.Type}) needs 'options'.";
        }
        else if (!this.UsesOptions && options.Count > 0)
        {
            yield return $"input '{input.Id}' ({input.Type}) doesn't take 'options'.";
        }

        if (options.Count != options.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            yield return $"input '{input.Id}' lists an option twice.";
        }

        if (options.Any(string.IsNullOrWhiteSpace))
        {
            yield return $"input '{input.Id}' has a blank option.";
        }

        if (!string.IsNullOrWhiteSpace(input.Default) && !input.HasTemplateDefault && (!this.UsesOptions || options.Count > 0))
        {
            string? problem = null;
            try
            {
                this.Normalize(input, input.Default);
            }
            catch (AflException ex)
            {
                problem = ex.Message;
            }

            if (problem is not null)
            {
                yield return $"input '{input.Id}' default: {problem}";
            }
        }
    }

    /// <summary>
    ///     Turns a raw answer into the text kept for the input, applying the default and checking required answers.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="raw">The answer given, or null.</param>
    /// <returns>The answer, or null for an optional input left blank.</returns>
    /// <exception cref="AflException">The answer is missing or invalid.</exception>
    public virtual string? Resolve(StepInput input, string? raw)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? input.Default : raw;
        if (string.IsNullOrWhiteSpace(value))
        {
            return input.Required ? throw Invalid(input, "needs an answer") : null;
        }

        return this.Normalize(input, value);
    }

    /// <summary>
    ///     Checks a non-blank answer and returns it in canonical form.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="value">The answer.</param>
    /// <returns>The canonical answer.</returns>
    /// <exception cref="AflException">The answer is invalid.</exception>
    public abstract string Normalize(StepInput input, string value);

    /// <summary>
    ///     Describes what a valid answer looks like, for prompts and help.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The description.</returns>
    public virtual string Describe(StepInput input)
    {
        return this.UsesOptions ? $"one of: {string.Join(", ", input.Options ?? [])}" : input.Type;
    }

    /// <summary>
    ///     Creates the error for an invalid answer.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="problem">What's wrong, e.g. "needs an answer".</param>
    /// <returns>The exception.</returns>
    protected static AflException Invalid(StepInput input, string problem)
    {
        var label = string.IsNullOrWhiteSpace(input.Label) ? input.Id : input.Label;
        return new AflException(AflErrorKind.InvalidInput, $"'{label}' {problem}.");
    }

    /// <summary>
    ///     Finds the option matching an answer, ignoring case.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="value">The answer.</param>
    /// <returns>The option as written in the workflow.</returns>
    /// <exception cref="AflException">No option matches.</exception>
    protected static string MatchOption(StepInput input, string value)
    {
        var trimmed = value.Trim();
        return input.Options?.FirstOrDefault(o => string.Equals(o.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
               ?? throw Invalid(input, $"must be one of: {string.Join(", ", input.Options ?? [])}");
    }
}

/// <summary>
///     A single line of text.
/// </summary>
[StepInputTypeRegistration(StepConstants.InputText)]
public class TextInputType : AbstractStepInputType
{
    /// <inheritdoc />
    public override StepInputEditor Editor => StepInputEditor.Text;

    /// <inheritdoc />
    public override string Normalize(StepInput input, string value)
    {
        return value.Trim();
    }
}

/// <summary>
///     Several lines of text, e.g. a summary or review notes.
/// </summary>
[StepInputTypeRegistration(StepConstants.InputMultiline)]
public class MultilineInputType : AbstractStepInputType
{
    /// <inheritdoc />
    public override StepInputEditor Editor => StepInputEditor.MultilineText;

    /// <inheritdoc />
    public override string Normalize(StepInput input, string value)
    {
        return value.ReplaceLineEndings("\n").Trim();
    }
}

/// <summary>
///     A number, kept in invariant form (e.g. <c>2.5</c>).
/// </summary>
[StepInputTypeRegistration(StepConstants.InputNumber)]
public class NumberInputType : AbstractStepInputType
{
    /// <inheritdoc />
    public override StepInputEditor Editor => StepInputEditor.Text;

    /// <inheritdoc />
    public override string Normalize(StepInput input, string value)
    {
        return decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : throw Invalid(input, "must be a number");
    }
}

/// <summary>
///     One of the input's options.
/// </summary>
[StepInputTypeRegistration(StepConstants.InputChoice)]
public class ChoiceInputType : AbstractStepInputType
{
    /// <inheritdoc />
    public override StepInputEditor Editor => StepInputEditor.Choice;

    /// <inheritdoc />
    public override bool UsesOptions => true;

    /// <inheritdoc />
    public override string Normalize(StepInput input, string value)
    {
        return MatchOption(input, value);
    }
}

/// <summary>
///     Any of the input's options, given and kept comma-separated in the order the workflow lists them. A required
///     multichoice needs at least one.
/// </summary>
[StepInputTypeRegistration(StepConstants.InputMultiChoice)]
public class MultiChoiceInputType : AbstractStepInputType
{
    /// <inheritdoc />
    public override StepInputEditor Editor => StepInputEditor.MultiChoice;

    /// <inheritdoc />
    public override bool UsesOptions => true;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(StepInput input)
    {
        foreach (var problem in base.Validate(input))
        {
            yield return problem;
        }

        if (input.Options?.Any(o => o.Contains(',')) == true)
        {
            yield return $"input '{input.Id}': multichoice options can't contain commas.";
        }
    }

    /// <inheritdoc />
    public override string Normalize(StepInput input, string value)
    {
        var picked = Split(value).Select(v => MatchOption(input, v)).ToHashSet();
        return string.Join(StepConstants.ListSeparator, input.Options!.Where(picked.Contains));
    }

    /// <inheritdoc />
    public override string Describe(StepInput input)
    {
        return $"any of: {string.Join(", ", input.Options ?? [])} (comma-separated)";
    }

    /// <summary>
    ///     Splits a comma-separated answer.
    /// </summary>
    /// <param name="value">The answer.</param>
    /// <returns>The picked options.</returns>
    public static IReadOnlyList<string> Split(string? value)
    {
        return (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
///     A yes/no question, kept as <c>yes</c> or <c>no</c>. Accepts y/n, true/false and 1/0 too.
/// </summary>
[StepInputTypeRegistration(StepConstants.InputConfirm)]
public class ConfirmInputType : AbstractStepInputType
{
    /// <summary>
    ///     The answers meaning yes.
    /// </summary>
    private static readonly string[] YesWords = [StepConstants.Yes, "y", "true", "1"];

    /// <summary>
    ///     The answers meaning no.
    /// </summary>
    private static readonly string[] NoWords = [StepConstants.No, "n", "false", "0"];

    /// <inheritdoc />
    public override StepInputEditor Editor => StepInputEditor.Toggle;

    /// <inheritdoc />
    public override string Normalize(StepInput input, string value)
    {
        var word = value.Trim().ToLowerInvariant();
        return YesWords.Contains(word) ? StepConstants.Yes
            : NoWords.Contains(word) ? StepConstants.No
            : throw Invalid(input, "must be yes or no");
    }

    /// <inheritdoc />
    public override string Describe(StepInput input)
    {
        return "yes or no";
    }
}
