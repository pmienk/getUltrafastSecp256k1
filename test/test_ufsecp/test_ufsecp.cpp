// Smoke test for the UltrafastSecp256k1-vc145 NuGet package.
//
// Verifies that:
//   - the include path injected by the .targets file is correct
//   - the linker finds the static (or shared) library
//
// Build all four configurations (Debug/Release x x64/Win32) to cover the
// full matrix of .targets conditions.

#include <ufsecp/ufsecp.h>
#include <ufsecp/ufsecp_version.h>

int main()
{
    return 0;
}
