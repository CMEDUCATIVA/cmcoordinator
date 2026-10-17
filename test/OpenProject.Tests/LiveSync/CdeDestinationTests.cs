using System;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class CdeDestinationTests
  {
    private const string Token = "test_connection_token_not_a_real_secret_123456";
    private const string Link = "https://cmproyectos.cmeducativa.es/projects/1/bcf/revit/channels/2/";
    [Theory]
    [InlineData("http://cmproyectos.cmeducativa.es/projects/1/bcf/revit/channels/2/")]
    [InlineData("https://other.example/projects/1/bcf/revit/channels/2/")]
    [InlineData("https://cmproyectos.cmeducativa.es/projects/1/bcf/revit/channels/2/?token=x")]
    [InlineData("https://user@cmproyectos.cmeducativa.es/projects/1/bcf/revit/channels/2/")]
    [InlineData("https://cmproyectos.cmeducativa.es/projects/1/bcf/")]
    public void RejectsUntrustedOrNonScopedLinks(string link)
      => Assert.Throws<ArgumentException>(() => new CdeDestination(link, Token));
    [Fact]
    public void VerificationBindsIdentityAndNeverDisplaysToken()
    {
      var candidate = new CdeDestination(Link, Token);
      Assert.Throws<InvalidOperationException>(() => candidate.Verified(new ReceiverStatus { UserId = 7, ProjectId = 1, ModelId = 8, ChannelId = 3 }));
      var verified = candidate.Verified(new ReceiverStatus { UserId = 7, ProjectId = 1, ModelId = 8, ChannelId = 2, ProjectName = "Proyecto", ModelName = "Modelo" });
      Assert.Equal(7, verified.UserId);
      Assert.Equal("Proyecto · Modelo", verified.Label);
      Assert.DoesNotContain(Token, verified.ToString());
      Assert.Equal(candidate.QueueKey, verified.QueueKey);
      Assert.NotEqual(candidate.QueueKey, new CdeDestination(Link.Replace("channels/2", "channels/3"), Token).QueueKey);
    }
  }
}
