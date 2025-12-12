// UtilsWindows_Patched.cpp - Patched version to fix NTSTATUS issues

// Include winternl.h or define NTSTATUS before everything else
#include <windows.h>
#include <winternl.h>

// Now include the original file
#include "amf/public/common/Windows/UtilsWindows.cpp"
