namespace OpenProject.Revit.LiveSync.Transport
{
  public static class LocalViewerEndpoint
  {
    // Each Revit version runs its own local viewer, so two Revit versions open at once do not share one server (nor its
    // data): 2022 → 5272 … 2027 → 5277. Earlier versions all used 5257, which these ports avoid.
#if Version2027
    public const int RevitYear = 2027;
#elif Version2026
    public const int RevitYear = 2026;
#elif Version2025
    public const int RevitYear = 2025;
#elif Version2024
    public const int RevitYear = 2024;
#elif Version2023
    public const int RevitYear = 2023;
#elif Version2022
    public const int RevitYear = 2022;
#elif Version2021
    public const int RevitYear = 2021;
#elif Version2020
    public const int RevitYear = 2020;
#elif Version2019
    public const int RevitYear = 2019;
#else
    public const int RevitYear = 2026;
#endif
    public const int Port = 5250 + RevitYear - 2000;
    public static readonly string Url = $"http://127.0.0.1:{Port}/";
  }
}
