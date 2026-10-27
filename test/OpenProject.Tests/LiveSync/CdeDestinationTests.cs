using System;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;

namespace OpenProject.Tests.LiveSync
{
  public class CdeDestinationTests
  {
    private const string Token = "test_connection_token_not_a_real_secret_123456";
    private const string Link = "https://erp.cmeducativa.es/projects/1/bcf/revit/channels/2/";
    [Theory]
    [InlineData("http://cmproyectos.cmeducativa.es/projects/1/bcf/revit/channels/2/")]
    [InlineData("http://erp.cmeducativa.es/projects/1/bcf/revit/channels/2/")]
    [InlineData("https://erp.cmeducativa.es/projects/1/bcf/revit/channels/2/?token=x")]
    [InlineData("https://user@erp.cmeducativa.es/projects/1/bcf/revit/channels/2/")]
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
      Assert.Equal("Proyecto", verified.ProjectName);
      Assert.Equal("Modelo", verified.ModelName);
      Assert.DoesNotContain(Token, verified.ToString());
      Assert.Equal(candidate.QueueKey, verified.QueueKey);
      Assert.NotEqual(candidate.QueueKey, new CdeDestination(Link.Replace("channels/2", "channels/3"), Token).QueueKey);
    }
    [Fact]
    public void AcceptsLinksFromSelectedHttpsInstances()
    {
      Assert.Equal("erp.cmeducativa.es", new CdeDestination(Link, Token).Address.Host);
      Assert.Equal("cmproyectos.cmeducativa.es",
        new CdeDestination("https://cmproyectos.cmeducativa.es/projects/1/bcf/revit/channels/2/", Token).Address.Host);
      Assert.Equal(8443, new CdeDestination("https://bim.example.org:8443/projects/1/bcf/revit/channels/2/", Token).Address.Port);
    }
  }
}
