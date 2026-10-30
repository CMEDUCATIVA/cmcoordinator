using OpenProject.Browser.WebViewIntegration;
using Xunit;

namespace OpenProject.Tests.WebViewIntegration
{
  public class CefCacheFolderTests
  {
    [Theory]
    [InlineData(@"C:\ProgramData\Autodesk\Revit\Addins\2026\Cmcoordinator\OpenProject.Browser\", "BrowserCache-2026")]
    [InlineData(@"C:\Program Files\Autodesk\Revit\Addins\2027\Cmcoordinator\OpenProject.Browser\", "BrowserCache-2027")]
    [InlineData(@"C:\Users\x\AppData\Roaming\Autodesk\REVIT\Addins\2025\OpenProject.Revit\", "BrowserCache-2025")]
    [InlineData(@"D:\Cmcoordinator\src\OpenProject.Browser\bin\x64\Debug\net48\", "BrowserCache")]
    [InlineData(null, "BrowserCache")]
    public void EachRevitVersionKeepsItsOwnBrowserCache(string browserDirectory, string expected)
      => Assert.Equal(expected, CefBrowserInitializer.CacheFolderName(browserDirectory));
  }
}
