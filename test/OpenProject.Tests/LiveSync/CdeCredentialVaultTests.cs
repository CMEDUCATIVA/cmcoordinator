using System.Linq;
using System.Reflection;
using System.Text;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class CdeCredentialVaultTests
  {
    [Fact]
    public void WindowsProtectionRoundTripsWithoutWritingUserConfiguration()
    {
      var type = typeof(CdeDestination).Assembly.GetType("OpenProject.Revit.LiveSync.Transport.CdeCredentialVault", true);
      var transform = type.GetMethod("Transform", BindingFlags.NonPublic | BindingFlags.Static);
      var plain = Encoding.UTF8.GetBytes("Credencial sintética de prueba · no es un token real");
      var encrypted = (byte[])transform.Invoke(null, new object[] { plain, true });
      Assert.False(plain.SequenceEqual(encrypted));
      var restored = (byte[])transform.Invoke(null, new object[] { encrypted, false });
      Assert.Equal(plain, restored);
    }
  }
}
