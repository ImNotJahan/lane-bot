using Lane.Tools.Identity;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The words a voice claim is proved with.
///
/// They exist because the other kind does not survive the trip. A claim spoken into a
/// microphone comes back through speech recognition, and a recogniser tuned for conversation
/// does not return "K7M2-QXBP" — it returns its best guess at English words that sounded like
/// that, which is not a code any more. So the code is made of words to begin with.
/// </summary>
public sealed class ProofPhraseTests
{
    private static string New() => ProofCodeShape.Spoken.New();

    [Fact]
    public void A_phrase_is_three_ordinary_words()
    {
        string[] words = New().Split(' ');

        Assert.Equal(3, words.Length);
        Assert.All(words, word => Assert.Contains(word, ProofWords.All));

        // Distinct, because a repeat is a word of entropy given away — and a phrase somebody
        // assumes they misheard.
        Assert.Equal(3, words.Distinct().Count());
    }

    [Fact]
    public void The_words_are_ones_a_recogniser_will_hand_back()
    {
        // Not a style rule. A one- or two-letter token is what a recogniser produces when it
        // has given up, and an apostrophe or a digit is a thing it renders differently every
        // time — either would make a phrase that cannot be said back.
        Assert.All(ProofWords.All, word =>
        {
            Assert.True(word.Length >= 4, word);
            Assert.True(word.All(char.IsAsciiLetterLower), word);
        });

        Assert.Equal(ProofWords.All.Count, ProofWords.All.Distinct().Count());
    }

    [Fact]
    public void No_word_is_two_others_run_together()
    {
        // Phrases are matched with the gaps removed, so that "pine cone" and "pinecone" are
        // the same phrase. That is only safe while no word in the list is itself a pair of
        // others: "candlestick" alongside "candle" and "stick" would make one phrase redeem
        // as another.
        HashSet<string> words = [.. ProofWords.All];

        Assert.All(ProofWords.All, word =>
        {
            for (int split = 1; split < word.Length; split++)
                Assert.False(
                    words.Contains(word[..split]) && words.Contains(word[split..]),
                    $"{word} is {word[..split]} + {word[split..]}");
        });
    }

    [Theory]
    [InlineData("monkey river candle")]
    [InlineData("Monkey, river, candle.")]                 // as a recogniser punctuates it
    [InlineData("  monkey   river   candle  ")]            // as a person retypes it
    [InlineData("monkey-river-candle")]                    // as somebody assumes it is written
    [InlineData("mon key river candle")]                   // as a compound word comes back split
    [InlineData("\"monkey river candle\"")]                // as a model relays it
    public void What_comes_back_is_the_same_phrase_however_it_was_written(string spoken)
    {
        Assert.Equal(
            ProofCodeShape.Spoken.Key("monkey river candle"),
            ProofCodeShape.Spoken.Key(spoken));
    }

    [Fact]
    public void Different_words_are_a_different_phrase()
    {
        Assert.NotEqual(
            ProofCodeShape.Spoken.Key("monkey river candle"),
            ProofCodeShape.Spoken.Key("monkey river kettle"));
    }

    [Fact]
    public void The_typed_kind_still_forgives_case_and_the_hyphen()
    {
        // link_identity is read off one screen and typed into another, so it keeps the dense
        // form — and the same forgiveness it always had.
        string code = ProofCodeShape.Typed.New();

        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);

        Assert.Equal(
            ProofCodeShape.Typed.Key(code),
            ProofCodeShape.Typed.Key(code.Replace("-", " ").ToLowerInvariant()));
    }
}
