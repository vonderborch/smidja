using Afl.Core.Runs;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One of the questions an AI left at the end of a step waiting for approval, on the Runs page, with the user's
///     answer to it.
/// </summary>
public class OpenQuestionDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="OpenQuestionDataContext" /> class.
    /// </summary>
    /// <param name="question">The question.</param>
    public OpenQuestionDataContext(OpenQuestion question)
    {
        this.Question = question;
    }

    /// <summary>
    ///     Gets the question.
    /// </summary>
    public OpenQuestion Question { get; }

    /// <summary>
    ///     Gets the question as shown: its number and its plain text.
    /// </summary>
    public string Label => $"{this.Question.Number}. {this.Question.PlainText}";

    /// <summary>
    ///     Gets or sets the user's answer; blank leaves the AI's assumption.
    /// </summary>
    public string Answer
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;
}
