namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class BcfierJavascriptInterop
  {
    private readonly JavaScriptBridge _javaScriptBridge;

    public BcfierJavascriptInterop(JavaScriptBridge javaScriptBridge)
    {
      _javaScriptBridge = javaScriptBridge;
    }

    public void SendMessageToRevit(string type, string trackingId, string payload)
    {
      _javaScriptBridge.SendMessageToRevit(type, trackingId, payload);
    }
  }
}
