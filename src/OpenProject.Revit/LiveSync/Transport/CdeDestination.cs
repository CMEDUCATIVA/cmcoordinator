using System;
using System.Text.RegularExpressions;

namespace OpenProject.Revit.LiveSync.Transport
{
  public sealed class CdeDestination
  {
    public Uri Address { get; }
    public string Token { get; }
    public long UserId { get; }
    public string Label { get; }
    public long ConnectionId { get; }
    public string QueueKey => "cde-" + Queue.SyncEncoding.Hash(ConnectionId > 0 ? Address.AbsoluteUri + "|" + UserId + "|" + ConnectionId : Address.AbsoluteUri).Substring(0, 24);
    public CdeDestination(string link, string token, long userId = 0, string label = null, long connectionId = 0)
    {
      if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
          uri.Host != "cmproyectos.cmeducativa.es" || !uri.IsDefaultPort ||
          uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
          !Regex.IsMatch(uri.AbsolutePath, @"^/projects/[1-9][0-9]*/bcf/revit/channels/[1-9][0-9]*/$"))
        throw new ArgumentException("Copia el enlace HTTPS generado en Conectar a Revit del CDE.");
      if (!Regex.IsMatch(token?.Trim() ?? "", @"^[A-Za-z0-9_-]{32,128}$"))
        throw new ArgumentException("Copia el token completo generado por el CDE.");
      ConnectionId = connectionId; Address = uri; Token = token.Trim(); UserId = userId; Label = label ?? "CDE pendiente de prueba";
    }
    public CdeDestination Verified(ReceiverStatus status)
    {
      if (status.UserId <= 0 || status.ProjectId <= 0 || status.ModelId <= 0 ||
          Address.AbsolutePath != $"/projects/{status.ProjectId}/bcf/revit/channels/{status.ChannelId}/")
        throw new InvalidOperationException("El receptor no confirmó la identidad del destino.");
      return new CdeDestination(Address.AbsoluteUri, Token, status.UserId, status.ProjectName + " · " + status.ModelName, status.ConnectionId);
    }
    public override string ToString() => Label; // Never include the bearer token in logs or UI diagnostics.
  }
}
