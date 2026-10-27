using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpenProject.Revit.LiveSync;
using OpenProject.Revit.LiveSync.Capture;
using OpenProject.Revit.LiveSync.Queue;
using OpenProject.Revit.LiveSync.Transport;
using Xunit;
namespace OpenProject.Tests.LiveSync
{
  public class DocumentIdentityTests
  {
    [Fact]
    public void LogicalIdentityIsNormalizedAndDistinct()
    {
      Assert.Equal(DocumentIdentityKey.Create("Document-A"), DocumentIdentityKey.Create(" document-a "));
      Assert.NotEqual(DocumentIdentityKey.Create("Document-A"), DocumentIdentityKey.Create("Document-B"));
      Assert.Throws<ArgumentException>(() => DocumentIdentityKey.Create(" "));
    }
    [Fact]
    public void IdentitySurvivesDurableQueueAndManifest()
    {
      using var folder = new OutboxTests.TestFolder();
      using var box = new SqliteOutbox(folder.Path);
      box.Save(new LiveSnapshot(DocumentIdentityKey.Create("document-a"), "Model", "3D", new Dictionary<string,LiveElement>(),
        documentId: "document-a", legacySourceId: new string('a',64)));
      var job = box.Prepare("session",1,null,true);
      Assert.Equal("document-a",job.DocumentId);
      Assert.Equal(3,job.Protocol);
      Assert.Equal(job.DocumentId,box.Active.DocumentId);
    }
    [Fact]
    public void QueueIsPersonalEvenWithSameModelAddress()
    {
      const string link = "https://erp.cmeducativa.es/projects/18/bcf/revit/channels/6/";
      var first = new CdeDestination(link,new string('a',32),10,"Model",1);
      var second = new CdeDestination(link,new string('b',32),11,"Model",2);
      Assert.NotEqual(first.QueueKey,second.QueueKey);
      Assert.Equal(first.QueueKey,new CdeDestination(link,new string('c',32),10,"Model",1).QueueKey);
    }
    [Fact]
    public void SavedConnectionRetainsHistoryIdentityAndReadsLegacyRecord()
    {
      const string link = "https://erp.cmeducativa.es/projects/18/bcf/revit/channels/6/";
      var verified = new CdeDestination(link,new string('a',32),10,"Model",17,"Project","Model");
      var restored = CdeCredentialVault.ParseRecord(CdeCredentialVault.SerializeRecord(verified));
      Assert.Equal(verified.QueueKey,restored.QueueKey);
      Assert.Equal(verified.UserId,restored.UserId);
      Assert.Equal(verified.ModelName,restored.ModelName);
      var legacy = CdeCredentialVault.ParseRecord("{\"link\":\"" + link + "\",\"token\":\"" + new string('a',32) + "\"}");
      Assert.Equal(0,legacy.UserId);
      Assert.Equal(0,legacy.ConnectionId);
    }
  }
}
