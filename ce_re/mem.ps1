Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Mem {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
"@
$PROC = 0x0010  # VM_READ only
function Open-Dino([int]$ProcessId) {
    $p = Get-Process -Id $ProcessId -ErrorAction Stop
    if ($p.ProcessName -ne "DINO") { throw "Expected DINO process" }
    $h = [Mem]::OpenProcess($PROC, $false, $p.Id)
    if ($h -eq [IntPtr]::Zero) { throw "OpenProcess failed" }
    return $h
}
function Read-Bytes([IntPtr]$h, [int64]$addr, [int]$len) {
    $buf = New-Object byte[] $len
    $read = [IntPtr]::Zero
    $ok = [Mem]::ReadProcessMemory($h, [IntPtr]$addr, $buf, $len, [ref]$read)
    if (-not $ok -or $read.ToInt64() -ne $len) { throw "RPM failed at 0x$("{0:X}" -f $addr)" }
    return $buf
}
function Read-U32([IntPtr]$h, [int64]$addr) {
    return [BitConverter]::ToUInt32((Read-Bytes $h $addr 4), 0)
}
function Read-AsciiZ([IntPtr]$h, [int64]$addr, [int]$max=64) {
    $b = Read-Bytes $h $addr $max
    $n = [Array]::IndexOf($b, [byte]0)
    if ($n -lt 0) { $n = $max }
    return [System.Text.Encoding]::ASCII.GetString($b, 0, $n)
}

function Close-Dino([IntPtr]$h) {
    if (-not [Mem]::CloseHandle($h)) { throw "CloseHandle failed" }
}
