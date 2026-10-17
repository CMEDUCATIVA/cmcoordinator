using System;

namespace OpenProject.Shared.Authentication
{
  public enum CdeSessionStatus { Unavailable, Authenticated, SignedOut }

  public sealed class CdeSessionObservation
  {
    public CdeSessionStatus Status { get; }
    public long UserId { get; }
    public CdeSessionObservation(CdeSessionStatus status, long userId = 0)
    {
      if (!Enum.IsDefined(typeof(CdeSessionStatus), status) || (status == CdeSessionStatus.Authenticated && userId <= 0))
        throw new ArgumentException("Estado de sesión inválido");
      Status = status; UserId = status == CdeSessionStatus.Authenticated ? userId : 0;
    }
  }

  // Confirmed logout revokes local access; closing the browser or losing connectivity does not.
  public sealed class CdeAccessState
  {
    private readonly CdeSessionLease _remote = new CdeSessionLease();
    public long LastSignedInUserId { get; private set; }
    public bool HasLocalAccess { get; private set; }
    public bool PanelRequested { get; private set; }
    public CdeSessionStatus Status { get; private set; } = CdeSessionStatus.Unavailable;
    public bool HasFreshCdeSession(DateTime utcNow) => Status == CdeSessionStatus.Authenticated && _remote.IsValid(utcNow);
    public void BeginBrowser(string nonce)
    { _remote.Reset(nonce); Status = CdeSessionStatus.Unavailable; }
    public void RequestPanel() => PanelRequested = true;
    public void PanelShown() => PanelRequested = false;
    public bool Accept(string nonce, long sequence, CdeSessionObservation observation, DateTime observedUtc, DateTime utcNow)
    {
      if (observation == null || !_remote.Accept(nonce, sequence, observation.UserId, observedUtc, utcNow)) return false;
      Status = observation.Status;
      if (Status == CdeSessionStatus.Authenticated)
      {
        if (!HasLocalAccess) PanelRequested = true;
        HasLocalAccess = true;
        LastSignedInUserId = observation.UserId;
      }
      else if (Status == CdeSessionStatus.SignedOut)
      { HasLocalAccess = false; PanelRequested = false; }
      return true;
    }
  }
}
