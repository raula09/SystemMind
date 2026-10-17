
using System;
using System.IO;

foreach (var d in DriveInfo.GetDrives())
{
    Console.WriteLine($"Name: {d.Name}, VolumeLabel: {d.VolumeLabel}, DriveType: {d.DriveType}, TotalSize: {d.TotalSize / (1024*1024*1024)}GB, Available: {d.AvailableFreeSpace / (1024*1024*1024)}GB, IsReady: {d.IsReady}");
}
