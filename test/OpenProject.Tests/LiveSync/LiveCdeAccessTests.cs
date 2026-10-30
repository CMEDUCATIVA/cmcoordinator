using System;
using System.Threading;
using System.Threading.Tasks;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  /// <summary>Reads the group of a real connection through the production transport.</summary>
  /// <remarks>Runs only when CMCOORDINATOR_LIVE_LINK, CMCOORDINATOR_LIVE_TOKEN and CMCOORDINATOR_LIVE_LEVEL are set
  /// (the audit passes them from a connection it creates and revokes); otherwise it has nothing to check.</remarks>
  public class LiveCdeAccessTests
  {
    [Fact]
    public async Task CdeReportsTheGroupOfThisConnection()
    {
      var link = Environment.GetEnvironmentVariable("CMCOORDINATOR_LIVE_LINK");
      var token = Environment.GetEnvironmentVariable("CMCOORDINATOR_LIVE_TOKEN");
      var expected = Environment.GetEnvironmentVariable("CMCOORDINATOR_LIVE_LEVEL");
      if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expected)) return;
      using var receiver = new SyncReceiver(destination: new CdeDestination(link, token));
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      var status = await receiver.StatusAsync(timeout.Token);
      var access = CdeModelAccess.From(status);
      Assert.True(status.Supports("modelLevels"), "El CDE debe anunciar la capacidad modelLevels");
      Assert.Equal(expected, access.Level);
      Assert.NotNull(status.ModelExists);
      Assert.Equal(expected != "reader", access.CanSynchronize);
      Assert.Equal(expected == "admin" && status.ModelExists == true, access.CanReplace);
      Assert.Equal(expected != "reader" && status.ModelExists == false, access.CanCreate);
    }
  }
}
