using System;
using Newtonsoft.Json.Linq;

namespace OpenProject.Shared.Authentication
{
  public static class CdeSessionPolicy
  {
    public static bool TryGetOrigin(string address, out string origin)
    {
      origin = null;
      if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
        || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
      origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
      return true;
    }
    public static bool IsCde(string address, string selectedInstance)
      => TryGetOrigin(address, out var origin) && TryGetOrigin(selectedInstance, out var selected)
        && string.Equals(origin, selected, StringComparison.OrdinalIgnoreCase);
    public static string CurrentUser(string origin)
      => TryGetOrigin(origin, out var canonical) ? canonical + "/api/v3/users/me"
        : throw new ArgumentException("Origen CDE HTTPS inválido.", nameof(origin));

    public static long ReadUser(string json)
    {
      try
      {
        var user = JObject.Parse(json);
        return (string)user["_type"] == "User" && (string)user["status"] == "active"
          && !string.IsNullOrWhiteSpace((string)user["login"])
          && user["id"]?.Type == JTokenType.Integer && (long)user["id"] > 0 ? (long)user["id"] : 0;
      }
      catch (Exception error) when (error is Newtonsoft.Json.JsonException || error is OverflowException || error is InvalidCastException || error is ArgumentException)
      { return 0; }
    }
  }

  // A heartbeat is scoped to one native browser launch; old messages cannot reopen a panel.
  public sealed class CdeSessionLease
  {
    private string _nonce;
    private long _sequence;
    private DateTime _validUntil;
    public long UserId { get; private set; }
    public bool IsValid(DateTime utcNow) => UserId > 0 && utcNow < _validUntil;
    public void Reset(string nonce)
    { _nonce = nonce; _sequence = 0; UserId = 0; _validUntil = DateTime.MinValue; }
    public bool Accept(string nonce, long sequence, long userId, DateTime observedUtc, DateTime utcNow)
    {
      if (string.IsNullOrEmpty(_nonce) || nonce != _nonce || sequence <= _sequence
        || observedUtc > utcNow.AddSeconds(2) || observedUtc < utcNow.AddSeconds(-15)) return false;
      _sequence = sequence; UserId = Math.Max(0, userId);
      _validUntil = observedUtc.AddSeconds(20); return true;
    }
  }
}
