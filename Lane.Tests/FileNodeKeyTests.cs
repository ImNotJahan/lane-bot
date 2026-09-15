using System.Security.Cryptography;
using Lane.Node.Sdk;
using Lane.Nodes.Protocol;
using Xunit;

namespace Lane.Tests;

public sealed class FileNodeKeyTests
{
    [Fact]
    public void Created_key_files_load_as_the_same_identity_and_are_never_overwritten()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lane-key-{Guid.NewGuid():n}.pem");

        try
        {
            using FileNodeKey created = FileNodeKey.Create(path);
            using FileNodeKey loaded  = FileNodeKey.Load(path);

            Assert.Equal(NodeProtocol.EcdsaP256Sha256, loaded.Identity.Algorithm);
            Assert.Equal(created.Identity, loaded.Identity);

            Assert.Throws<IOException>(() => FileNodeKey.Create(path));

            using (FileNodeKey again = FileNodeKey.Load(path))
                Assert.Equal(created.Identity, again.Identity);

            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            byte[] challenge = RandomNumberGenerator.GetBytes(32);

            Assert.True(NodeProtocol.VerifySignIn(loaded.Identity, challenge, created.SignSignIn(challenge)));
            Assert.False(NodeProtocol.VerifySignIn(loaded.Identity, RandomNumberGenerator.GetBytes(32), created.SignSignIn(challenge)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loading_a_file_that_is_not_a_key_says_so()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lane-key-{Guid.NewGuid():n}.pem");

        try
        {
            File.WriteAllText(path, "not a key");

            Assert.Throws<ArgumentException>(() => FileNodeKey.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
