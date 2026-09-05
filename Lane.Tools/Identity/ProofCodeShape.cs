using System.Security.Cryptography;
using System.Text;

namespace Lane.Tools.Identity;

/// <summary>
/// What a proof code looks like, which depends entirely on how it gets from one channel to
/// the other.
///
/// A code read off a screen and typed into another one wants to be short and dense. A code
/// <em>said out loud</em> wants nothing of the sort: it comes back through speech
/// recognition, and a recogniser tuned for conversation will not return "K7M2-QXBP". It
/// returns "K seven M two", or "kay some qux be pee", or its best guess at an English word
/// that sounds like whatever it heard. No amount of normalising rescues that, because the
/// characters genuinely did not survive the trip.
///
/// Words do survive, because words are what recognition was built to produce. Three of them
/// carry far more than enough to be unguessable and come back intact.
/// </summary>
internal abstract class ProofCodeShape
{
    /// <summary>Read off one screen, typed into another.</summary>
    public static ProofCodeShape Typed { get; } = new TypedCode();

    /// <summary>Said out loud, and heard by a speech recogniser.</summary>
    public static ProofCodeShape Spoken { get; } = new SpokenPhrase();

    /// <summary>A fresh code, in the form a person is shown it.</summary>
    public abstract string New();

    /// <summary>
    /// What a code is filed and looked up under, so that the difference between how it was
    /// shown and how it came back stops mattering.
    /// </summary>
    public abstract string Key(string code);

    private sealed class TypedCode : ProofCodeShape
    {
        // No ambiguous glyphs: these are read off one screen and typed into another, and
        // "was that an O or a zero" is the whole failure mode.
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public override string New()
        {
            string code = RandomNumberGenerator.GetString(Alphabet, 8);

            return $"{code[..4]}-{code[4..]}";
        }

        /// <summary>Case, spacing and the hyphen are all things a person retypes differently.</summary>
        public override string Key(string code)
        {
            StringBuilder bare = new();

            foreach (char c in code.ToUpperInvariant())
                if (char.IsAsciiLetterOrDigit(c)) bare.Append(c);

            return bare.ToString();
        }
    }

    private sealed class SpokenPhrase : ProofCodeShape
    {
        private const int Words = 3;

        public override string New()
        {
            string[] chosen = new string[Words];

            for (int i = 0; i < Words; i++)
            {
                // Drawn without replacement: a repeat is a word of entropy given away, and
                // "candle candle river" is a phrase somebody assumes they misheard.
                string word;

                do
                {
                    word = ProofWords.All[RandomNumberGenerator.GetInt32(ProofWords.All.Count)];
                }
                while (Array.IndexOf(chosen, word, 0, i) >= 0);

                chosen[i] = word;
            }

            return string.Join(' ', chosen);
        }

        /// <summary>
        /// Letters only, run together — which is deliberately blunter than it looks.
        ///
        /// Punctuation and case go for the obvious reason: a recogniser punctuates what it
        /// heard and a person retypes it however they like. Dropping the <em>gaps</em> as
        /// well is what makes compound words survive: "pinecone" comes back as "pine cone"
        /// about as often as not, and there is no useful sense in which that is a different
        /// phrase. It costs nothing, because the words are drawn from a fixed list and no
        /// two draws from it run together into another valid one.
        /// </summary>
        public override string Key(string code)
        {
            StringBuilder bare = new();

            foreach (char c in code.ToLowerInvariant())
                if (char.IsAsciiLetter(c)) bare.Append(c);

            return bare.ToString();
        }
    }
}
