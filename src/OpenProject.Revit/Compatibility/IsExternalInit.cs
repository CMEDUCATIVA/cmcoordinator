#if NET48 || NET472
namespace System.Runtime.CompilerServices
{
  // C# init-only setters need this marker when targeting the legacy
  // .NET Framework used by Revit 2022/2024.
  internal sealed class IsExternalInit
  {
  }
}
#endif
