using System.Text;
using System.Text.RegularExpressions;
using Afl.Core.Exceptions;

namespace Afl.Core.Runs;

/// <summary>
///     A question an AI left for the user at the end of its output, under a heading such as "Open questions" or
///     "Questions for you", which the user can answer to send the step back. Each item of the list under the heading is
///     one question. The heading must be the output's last: questions under an earlier one are part of the work.
/// </summary>
public partial class OpenQuestion
{
    /// <summary>
    ///     Gets the question's number: its own number in a numbered list, otherwise its place in the list (from 1).
    /// </summary>
    public int Number { get; init; }

    /// <summary>
    ///     Gets the question as the AI wrote it, markdown included.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the question without its bold and italic markers, for showing as plain text.
    /// </summary>
    public string PlainText => Emphasis().Replace(this.Text, "${text}");

    /// <summary>
    ///     Finds the open questions at the end of an AI's output.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <returns>The questions in order, or an empty list when it has none.</returns>
    public static IReadOnlyList<OpenQuestion> Find(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output.ReplaceLineEndings("\n").Split('\n');
        // The questions end the output: a questions heading with another heading after it (say, a document's
        // "Risks and open questions" section) is part of the work, not questions for the user.
        var start = Array.FindLastIndex(lines, l => QuestionsHeading().IsMatch(l));
        if (start < 0 || Array.FindLastIndex(lines, l => Heading().IsMatch(l)) > start)
        {
            return [];
        }

        var questions = new List<(int Number, StringBuilder Text)>();
        var afterBlank = false;
        foreach (var line in lines.Skip(start + 1))
        {
            if (Heading().IsMatch(line) || line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                afterBlank = true;
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                var number = int.TryParse(item.Groups["number"].Value, out var n) ? n : questions.Count + 1;
                questions.Add((number, new StringBuilder(item.Groups["text"].Value.Trim())));
            }
            else if (questions.Count > 0 && (!afterBlank || char.IsWhiteSpace(line[0])))
            {
                // A wrapped or indented line goes on with the item above it.
                questions[^1].Text.Append(' ').Append(line.Trim());
            }
            else if (questions.Count > 0)
            {
                // A paragraph after the list ends it.
                break;
            }

            afterBlank = false;
        }

        return questions
            .Where(q => q.Text.Length > 0)
            .Select(q => new OpenQuestion { Number = q.Number, Text = q.Text.ToString() })
            .ToList();
    }

    /// <summary>
    ///     Writes the user's answers as feedback for the step: each question with its answer, the unanswered ones told
    ///     to keep the AI's assumption, then the user's note.
    /// </summary>
    /// <param name="questions">The step's open questions.</param>
    /// <param name="answers">The answers by question number; blank answers count as unanswered.</param>
    /// <param name="note">Anything else the user wants changed, if anything.</param>
    /// <returns>The feedback.</returns>
    /// <exception cref="AflException">When an answer is for a question that isn't there, or nothing was answered.</exception>
    public static string FormatAnswers(IReadOnlyList<OpenQuestion> questions, IReadOnlyDictionary<int, string> answers,
        string? note)
    {
        if (answers.Keys.FirstOrDefault(n => questions.All(q => q.Number != n)) is var missing and not 0)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"There's no open question {missing}; the step asks {string.Join(", ", questions.Select(q => q.Number))}.");
        }

        var answered = questions.Where(q => answers.TryGetValue(q.Number, out var a) && !string.IsNullOrWhiteSpace(a)).ToList();
        if (answered.Count == 0 && string.IsNullOrWhiteSpace(note))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Answer at least one question, or add a note.");
        }

        var text = new StringBuilder("My answers to your open questions:\n");
        foreach (var question in questions)
        {
            var answer = answered.Contains(question) ? answers[question.Number].Trim() : null;
            text.Append('\n').Append(question.Number).Append(". ").Append(question.PlainText).Append('\n')
                .Append("   Answer: ").Append(answer ?? "not answered; keep your assumption.").Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            text.Append("\nAlso: ").Append(note.Trim()).Append('\n');
        }

        return text.Append("\nRedo the step with these answers, and drop the questions they settle.").ToString();
    }

    /// <summary>
    ///     A markdown heading, or a line that is only bold text, that starts with "Questions" or "Open questions" (so a
    ///     document's "Risks and open questions" section isn't one).
    /// </summary>
    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+(?:open\s+)?questions?\b.*|\*\*(?:open\s+)?questions?\b[^*]*\*\*:?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionsHeading();

    /// <summary>
    ///     Any markdown heading.
    /// </summary>
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s")]
    private static partial Regex Heading();

    /// <summary>
    ///     A numbered or bulleted list item.
    /// </summary>
    [GeneratedRegex(@"^\s{0,3}(?:(?<number>\d+)[.)]|[-*+])\s+(?<text>.*)$")]
    private static partial Regex ListItem();

    /// <summary>
    ///     Bold or italic text.
    /// </summary>
    [GeneratedRegex(@"(\*\*|__)(?<text>.+?)\1|(?<![\w*])\*(?<text>[^*\s][^*]*?)\*(?![\w*])")]
    private static partial Regex Emphasis();
}
