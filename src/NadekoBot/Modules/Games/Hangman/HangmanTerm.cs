#nullable disable
using YamlDotNet.Serialization;

namespace NadekoBot.Modules.Games.Hangman;

public sealed class HangmanTerm
{
    public string Word { get; set; }
    public string ImageUrl { get; set; }
}