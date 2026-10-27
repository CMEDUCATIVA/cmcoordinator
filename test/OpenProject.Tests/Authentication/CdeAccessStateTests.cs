using System;
using OpenProject.Shared.Authentication;
using Xunit;

namespace OpenProject.Tests.Authentication
{
  public class CdeAccessStateTests
  {
    [Theory]
    [InlineData(CdeSessionStatus.Unavailable)]
    [InlineData(CdeSessionStatus.SignedOut)]
    public void NoLocalAccessBeforeTheFirstVerifiedIdentity(CdeSessionStatus status)
    {
      var state = new CdeAccessState(); var now = DateTime.UtcNow;
      state.BeginBrowser("one"); state.RequestPanel();
      Assert.True(state.Accept("one", 1, new CdeSessionObservation(status), now, now));
      Assert.False(state.HasLocalAccess);
      Assert.False(state.HasFreshCdeSession(now));
    }

    [Fact]
    public void ConnectivityDoesNotRemoveLocalAccessOrReopenThePanel()
    {
      var state = SignedIn(); var now = DateTime.UtcNow;
      Assert.True(state.PanelRequested); state.PanelShown();
      state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.Unavailable), now, now);
      Assert.True(state.HasLocalAccess);
      Assert.False(state.HasFreshCdeSession(now));
      Assert.False(state.PanelRequested);
      state.Accept("one", 3, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now);
      Assert.True(state.HasFreshCdeSession(now));
      Assert.False(state.PanelRequested, "Renewal must respect manual panel closure");
    }

    [Fact]
    public void ConfirmedLogoutRevokesAccessAndCancelsPanelOpening()
    {
      var state = SignedIn(); var now = DateTime.UtcNow;
      state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.SignedOut), now, now);
      Assert.False(state.HasLocalAccess); Assert.False(state.PanelRequested);
      Assert.False(state.HasFreshCdeSession(now));
      Assert.Equal(42, state.LastSignedInUserId);
    }

    [Fact]
    public void BrowserRestartAndNetworkFailureCannotRestoreLoggedOutAccess()
    {
      var state = SignedIn(); var now = DateTime.UtcNow;
      state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.SignedOut), now, now);
      state.BeginBrowser("two");
      state.Accept("two", 1, new CdeSessionObservation(CdeSessionStatus.Unavailable), now, now);
      Assert.False(state.HasLocalAccess); Assert.False(state.PanelRequested);
    }

    [Fact]
    public void VerifiedLoginAfterLogoutRequestsThePanelAgain()
    {
      var state = SignedIn(); var now = DateTime.UtcNow; state.PanelShown();
      state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.SignedOut), now, now);
      state.Accept("one", 3, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now);
      Assert.True(state.HasLocalAccess); Assert.True(state.PanelRequested);
      state.PanelShown();
      state.Accept("one", 4, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now);
      Assert.False(state.PanelRequested);
    }

    [Fact]
    public void DelayedLogoutCannotHideARecentlyValidatedSession()
    {
      var state = SignedIn(); var now = DateTime.UtcNow;
      state.Accept("one", 5, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now);
      Assert.False(state.Accept("one", 4, new CdeSessionObservation(CdeSessionStatus.SignedOut), now, now));
      Assert.True(state.HasLocalAccess); Assert.True(state.HasFreshCdeSession(now));
    }

    [Fact]
    public void LeaseExpiryAndBrowserRestartPreserveLocalWork()
    {
      var state = SignedIn(); state.PanelShown(); var later = DateTime.UtcNow.AddMinutes(5);
      Assert.False(state.HasFreshCdeSession(later));
      Assert.True(state.HasLocalAccess);
      state.BeginBrowser("two");
      Assert.False(state.HasFreshCdeSession(DateTime.UtcNow));
      Assert.True(state.HasLocalAccess); Assert.False(state.PanelRequested);
      Assert.False(state.Accept("one", 100, new CdeSessionObservation(CdeSessionStatus.Authenticated, 77), later, later));
      Assert.Equal(42, state.LastSignedInUserId);
      Assert.True(state.Accept("two", 1, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), later, later));
      Assert.True(state.HasFreshCdeSession(later)); Assert.False(state.PanelRequested);
      state.RequestPanel(); Assert.True(state.PanelRequested);
    }

    [Fact]
    public void RepeatedNavigationFailuresDoNotTogglePanelRequests()
    {
      var state = SignedIn(); state.PanelShown(); var now = DateTime.UtcNow;
      for (var i = 2; i < 42; i++)
      {
        var moment = now.AddSeconds(i * 5);
        var status = i % 2 == 0 ? CdeSessionStatus.Unavailable : CdeSessionStatus.Authenticated;
        state.Accept("one", i, new CdeSessionObservation(status, 42), moment, moment);
        Assert.True(state.HasLocalAccess); Assert.False(state.PanelRequested);
        Assert.Equal(status == CdeSessionStatus.Authenticated, state.HasFreshCdeSession(moment));
      }
    }

    [Fact]
    public void ReplayedOrStaleIdentityCannotUnlockLocalAccess()
    {
      var state = new CdeAccessState(); var now = DateTime.UtcNow;
      state.BeginBrowser("one");
      Assert.False(state.Accept("other", 1, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now));
      Assert.False(state.Accept("one", 1, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now.AddMinutes(-1), now));
      Assert.False(state.HasLocalAccess);
      state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.SignedOut), now, now);
      Assert.False(state.Accept("one", 1, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now));
      Assert.False(state.HasLocalAccess);
    }

    [Fact]
    public void AccountChangeRemainsDistinguishableFromConnectivityLoss()
    {
      var state = SignedIn(); state.PanelShown(); var now = DateTime.UtcNow;
      state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.Unavailable), now, now);
      Assert.Equal(42, state.LastSignedInUserId);
      state.Accept("one", 3, new CdeSessionObservation(CdeSessionStatus.Authenticated, 77), now, now);
      Assert.Equal(77, state.LastSignedInUserId); Assert.True(state.HasLocalAccess);
    }
    [Fact]
    public void SameNumericUserAtAnotherInstanceChangesIdentity()
    {
      var state = new CdeAccessState(); var now = DateTime.UtcNow;
      state.BeginBrowser("one");
      Assert.True(state.Accept("one", 1, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42,
        "https://cmproyectos.cmeducativa.es"), now, now));
      Assert.Equal("https://cmproyectos.cmeducativa.es", state.LastSignedInOrigin);
      Assert.True(state.Accept("one", 2, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42,
        "https://erp.cmeducativa.es"), now, now));
      Assert.Equal("https://erp.cmeducativa.es", state.LastSignedInOrigin);
    }
    private static CdeAccessState SignedIn()
    {
      var state = new CdeAccessState(); var now = DateTime.UtcNow;
      state.BeginBrowser("one");
      Assert.True(state.Accept("one", 1, new CdeSessionObservation(CdeSessionStatus.Authenticated, 42), now, now));
      return state;
    }
  }
}
