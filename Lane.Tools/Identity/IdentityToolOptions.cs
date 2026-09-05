namespace Lane.Tools.Identity;

/// <summary>
/// How hard Lane makes it to say who you are.
/// </summary>
public sealed class IdentityToolOptions
{
    /// <summary>
    /// Whether joining two accounts, or attaching a voice to a person, has to be proved.
    ///
    /// On — the default, and what the rest of the identity code is written around — Lane
    /// hands out something on one channel that has to come back on the other, and neither
    /// half can act on anybody but the participant actually present in that turn. She never
    /// takes a claim of identity from the model, because the model has nothing to go on
    /// beyond somebody having said so.
    ///
    /// Off, she does take it. The two halves still have to happen on the two channels — she
    /// will not link an account nobody is speaking from, or bind a voice that is not the one
    /// talking to her — but the code between them goes away, so being present on both is the
    /// whole of the proof. That is a reasonable trade in a house with one person in it and a
    /// bad one anywhere a stranger can reach her: it costs a bystander nothing to complete a
    /// link somebody else started, and a wrong link merges two people's memories in a way
    /// nothing here can unpick afterwards.
    /// </summary>
    public bool RequireProof { get; set; } = true;
}
