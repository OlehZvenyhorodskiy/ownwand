# **Advanced Engineering Report: System-Level Memory Manipulation, Runtime Detouring, and ESP Implementations in Unity and Unreal Engine Games**

The engineering behind host-side game trainers involves navigating low-level operating system architectures, dynamically manipulating managed and native runtimes, and applying real-time computer graphics math. When building a unified WPF-based application like OwnWand under the .NET 8 framework, developers must coordinate multiple subsystems. These include Windows security tokens, native memory pages, assemblies in Unity (Mono and IL2CPP), and the relative structures of Unreal Engine's compiled virtual address space. This report analyzes these technical areas, offering actionable architectural strategies and complete implementation models for professional reverse engineers.

## **Technical Architecture of Windows Process Attachment and Memory Access**

Interacting with an active user-mode process on Windows requires adhering to the operating system's security model, privilege levels, and thread scheduling boundaries. External trainers must acquire process handles with precise permissions to perform actions using the Win32 API.

\+------------------+     1\. Adjust Token     \+-------------------------+  
| OwnWand Trainer  |------------------------\>| Local LSASS Security    |  
| (Admin Context)  |                         | (Acquire SeDebugPriv)   |  
\+------------------+                         \+-------------------------+  
        |                                                 |  
        | 2\. OpenProcess(PROCESS\_ALL\_ACCESS)              | 3\. Returns Privilege  
        v                                                 v  
\+----------------------------------------------------------------------+  
| Windows Kernel (DACL & ObRegisterCallbacks Verification)             |  
\+----------------------------------------------------------------------+  
        |  
        | 4\. Granted Process Handle  
        v  
\+------------------+     5\. VirtualProtectEx      \+--------------------+  
| OwnWand Handle   |-----------------------------\>| Target Game Memory |  
| (Read/Write VM)  |                              | (Code & Heap Pages)|  
\+------------------+                              \+--------------------+

### **Analysis of API Obstruction Factors**

When game trainers attempt to attach to target binaries (especially shipping builds), several distinct system mechanisms can obstruct APIs like OpenProcess, ReadProcessMemory, and WriteProcessMemory. The following table details these core system obstacles and their corresponding remediation vectors:

| Operating System Barrier | Primary Kernel Mechanism | Impact on Trainer Operation | Technical Resolution |
| :---- | :---- | :---- | :---- |
| **Integrity Level Mismatch** | User Account Control (UAC) / User Interface Privilege Isolation (UIPI)1 | Elevates target game context above the trainer, causing OpenProcess to fail with ERROR\_ACCESS\_DENIED (0x5). | Enforce application self-elevation to High Integrity via application manifests1. |
| **Handle Stripping** | Driver-level callback filtering via ObRegisterCallbacks \[cite: 2\] | Intercepts system calls, stripping PROCESS\_VM\_READ and PROCESS\_VM\_WRITE access rights from returned handles. | Execute modifications using dynamic DKOM handle restoration or kernel-mode drivers2. |
| **DACL Modifications** | Discretionary Access Control List (DACL) security alterations | Strips access rights from administrative groups, preventing user-mode attachment. | Programmatically override the target process DACL using system security APIs. |
| **Protected Processes (PP / PPL)** | Protected Process Light (PPL) signature validation | Restricts user-mode process handle creation, preventing attachment by non-signed binaries. | Bypass signature verification checks using kernel-mode drivers or signed execution wrappers. |

### **Process Attachment Engine Implementation**

To establish a highly robust connection model, the trainer must execute within an elevated security context. The systems engineer must verify that the training application runs with elevated integrity. This is initially requested by embedding a standard application manifest into the WPF binary:

XML  
\<?xml version="1.0" encoding="utf-8"?\>  
\<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"\>  
  \<assemblyIdentity version="1.0.0.0" name="OwnWand.App"/\>  
  \<trustInfo xmlns="urn:schemas-microsoft-com:asm.v2"\>  
    \<security\>  
      \<requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3"\>  
        \<requestedExecutionLevel level="requireAdministrator" uiAccess="false" /\>  
      \</requestedPrivileges\>  
    \</security\>  
  \</trustInfo\>  
\</assembly\>

When elevation is secured, the application must programmatically adjust its token privileges to acquire SeDebugPrivilege. This privilege allows an administrator-level thread to open a handle to another process regardless of its security descriptor. The following C\# helper implementation demonstrates how to programmatically adjust token privileges, identify target process architectures (WOW64 check), and establish high-integrity memory handles:

C\#  
using System;  
using System.Diagnostics;  
using System.Runtime.InteropServices;  
using System.Security.Principal;

public static class ProcessAttachmentEngine  
{  
    \[Flags\]  
    public enum ProcessAccessFlags : uint  
    {  
        PROCESS\_VM\_READ \= 0x0010,  
        PROCESS\_VM\_WRITE \= 0x0020,  
        PROCESS\_VM\_OPERATION \= 0x0008,  
        PROCESS\_QUERY\_INFORMATION \= 0x0400,  
        PROCESS\_ALL\_ACCESS \= 0x001F0FFF  
    }

    \[StructLayout(LayoutKind.Sequential)\]  
    public struct LUID  
    {  
        public uint LowPart;  
        public int HighPart;  
    }

    \[StructLayout(LayoutKind.Sequential)\]  
    public struct LUID\_AND\_ATTRIBUTES  
    {  
        public LUID Luid;  
        public uint Attributes;  
    }

    \[StructLayout(LayoutKind.Sequential)\]  
    public struct TOKEN\_PRIVILEGES  
    {  
        public uint PrivilegeCount;  
        public LUID\_AND\_ATTRIBUTES Privileges;  
    }

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    public static extern bool CloseHandle(IntPtr hObject);

    \[DllImport("advapi32.dll", SetLastError \= true)\]  
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    \[DllImport("advapi32.dll", SetLastError \= true, CharSet \= CharSet.Auto)\]  
    public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

    \[DllImport("advapi32.dll", SetLastError \= true)\]  
    public static extern bool AdjustTokenPrivileges(  
        IntPtr TokenHandle,  
        bool DisableAllPrivileges,  
        ref TOKEN\_PRIVILEGES NewState,  
        uint BufferLength,  
        IntPtr PreviousState,  
        IntPtr ReturnLength);

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    public static extern bool IsWow64Process(IntPtr hProcess, out bool lpWow64Process);

    private const string SE\_DEBUG\_NAME \= "SeDebugPrivilege";  
    private const uint TOKEN\_ADJUST\_PRIVILEGES \= 0x0020;  
    private const uint TOKEN\_QUERY \= 0x0008;  
    private const uint SE\_PRIVILEGE\_ENABLED \= 0x00000002;

    public static bool EnableDebugPrivilege()  
    {  
        IntPtr hToken;  
        IntPtr hCurrentProcess \= Process.GetCurrentProcess().Handle;

        if (\!OpenProcessToken(hCurrentProcess, TOKEN\_ADJUST\_PRIVILEGES | TOKEN\_QUERY, out hToken))  
            return false;

        try  
        {  
            LUID luid;  
            if (\!LookupPrivilegeValue(null, SE\_DEBUG\_NAME, out luid))  
                return false;

            TOKEN\_PRIVILEGES tp \= new TOKEN\_PRIVILEGES  
            {  
                PrivilegeCount \= 1,  
                Privileges \= new LUID\_AND\_ATTRIBUTES  
                {  
                    Luid \= luid,  
                    Attributes \= SE\_PRIVILEGE\_ENABLED  
                }  
            };

            if (\!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))  
            {  
                int error \= Marshal.GetLastWin32Error();  
                return false;  
            }

            return true;  
        }  
        finally  
        {  
            CloseHandle(hToken);  
        }  
    }

    public static IntPtr AttachToTarget(int processId, out string architecture)  
    {  
        architecture \= "Unknown";  
        if (\!EnableDebugPrivilege())  
        {  
            return IntPtr.Zero;  
        }

        IntPtr processHandle \= OpenProcess(  
            (uint)(ProcessAccessFlags.PROCESS\_VM\_READ |   
                   ProcessAccessFlags.PROCESS\_VM\_WRITE |   
                   ProcessAccessFlags.PROCESS\_VM\_OPERATION |   
                   ProcessAccessFlags.PROCESS\_QUERY\_INFORMATION),   
            false,   
            processId  
        );

        if (processHandle \== IntPtr.Zero)  
        {  
            return IntPtr.Zero;  
        }

        if (IsWow64Process(processHandle, out bool isWow64))  
        {  
            architecture \= isWow64 ? "x86 (WOW64)" : "x64 Native";  
        }

        return processHandle;  
    }  
}

## **Direct Host-Side Memory Patching inside Compiled Native Unreal Engine Binaries**

Unlike managed environments where structures and type symbols are preserved, shipping compiles of Unreal Engine applications natively optimize logic directly into compiled x86\_64 machine code instructions3. Structures, dynamic arrays, and functions are represented as raw relative displacements or precompiled offsets. To safely execute modifications on shipping binaries like Backrooms-Win64-Shipping.exe4, the trainer must dynamically compute target addresses and manipulate memory access flags safely.

### **Memory Safety, Memory Protections, and Alignment Boundaries**

In modern architectures, memory execution is enforced via physical translation tables and CPU register control. Executable segments are flagged as read-only (PAGE\_EXECUTE\_READ), meaning any direct overwrite attempts will trigger a system protection fault. To modify these segments:

1. **Alter Protection Flags**: Call the Win32 VirtualProtectEx API to change the target code segment's access flags to read-write-execute (PAGE\_EXECUTE\_READWRITE).  
2. **Execute Write**: Write the new machine code instructions using WriteProcessMemory.  
3. **Restore Permissions**: Immediately restore the original memory page protection flags to prevent detection or instability.  
4. **Invalidate Instruction Cache**: Call the Win32 FlushInstructionCache API. This forces the CPU core to drop pre-cached instructions and read the newly modified memory, preventing execution mismatch.

C\#  
\[DllImport("kernel32.dll", SetLastError \= true)\]  
public static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

\[DllImport("kernel32.dll")\]  
public static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize);

### **Signature Scanning Engine with Boundary Validation**

Hardcoded memory offsets are fragile and often break during minor game updates or patches. Using an Array of Bytes (AOB) signature scanning engine to inspect the virtual address space is a more robust approach.  
The following class performs a high-performance memory scan. It uses VirtualQueryEx to safely skip uncommitted pages or pages protected by guard flags (PAGE\_GUARD), which would otherwise cause memory access violations:

C\#  
public static class SignatureScanner  
{  
    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, \[Out\] byte\[\] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY\_BASIC\_INFORMATION lpBuffer, int dwLength);

    \[StructLayout(LayoutKind.Sequential)\]  
    private struct MEMORY\_BASIC\_INFORMATION  
    {  
        public IntPtr BaseAddress;  
        public IntPtr AllocationBase;  
        public uint AllocationProtect;  
        public IntPtr RegionSize;  
        public uint State;  
        public uint Protect;  
        public uint Type;  
    }

    private const uint MEM\_COMMIT \= 0x1000;  
    private const uint PAGE\_GUARD \= 0x100;  
    private const uint PAGE\_NOACCESS \= 0x01;

    public static IntPtr Scan(IntPtr hProcess, string pattern)  
    {  
        ParsePattern(pattern, out byte\[\] signature, out bool\[\] mask);  
        long searchMax \= 0x7FFFFFFFFFFFFFFF;  
        long currentAddress \= 0x0;

        while (currentAddress \< searchMax)  
        {  
            if (VirtualQueryEx(hProcess, (IntPtr)currentAddress, out MEMORY\_BASIC\_INFORMATION mbi, Marshal.SizeOf(typeof(MEMORY\_BASIC\_INFORMATION))) \== 0\)  
                break;

            bool isReadable \= (mbi.State \== MEM\_COMMIT) &&  
                               ((mbi.Protect & PAGE\_GUARD) \== 0\) &&  
                               ((mbi.Protect & PAGE\_NOACCESS) \== 0\) &&  
                               (((mbi.Protect & 0x02) \!= 0\) || ((mbi.Protect & 0x04) \!= 0\) || ((mbi.Protect & 0x20) \!= 0\) || ((mbi.Protect & 0x40) \!= 0));

            if (isReadable)  
            {  
                byte\[\] buffer \= new byte\[(int)mbi.RegionSize\];  
                if (ReadProcessMemory(hProcess, mbi.BaseAddress, buffer, buffer.Length, out IntPtr read))  
                {  
                    int index \= FindPattern(buffer, signature, mask);  
                    if (index \!= \-1)  
                    {  
                        return (IntPtr)((long)mbi.BaseAddress \+ index);  
                    }  
                }  
            }  
            currentAddress \= (long)mbi.BaseAddress \+ (long)mbi.RegionSize;  
        }  
        return IntPtr.Zero;  
    }

    private static void ParsePattern(string pattern, out byte\[\] signature, out bool\[\] mask)  
    {  
        string\[\] split \= pattern.Split(' ');  
        signature \= new byte\[split.Length\];  
        mask \= new bool\[split.Length\];

        for (int i \= 0; i \< split.Length; i++)  
        {  
            if (split\[i\] \== "?" || split\[i\] \== "??")  
            {  
                signature\[i\] \= 0x00;  
                mask\[i\] \= false;  
            }  
            else  
            {  
                signature\[i\] \= Convert.ToByte(split\[i\], 16);  
                mask\[i\] \= true;  
            }  
        }  
    }

    private static int FindPattern(byte\[\] buffer, byte\[\] signature, bool\[\] mask)  
    {  
        for (int i \= 0; i \< buffer.Length \- signature.Length; i++)  
        {  
            bool found \= true;  
            for (int j \= 0; j \< signature.Length; j++)  
            {  
                if (mask\[j\] && buffer\[i \+ j\] \!= signature\[j\])  
                {  
                    found \= false;  
                    break;  
                }  
            }  
            if (found) return i;  
        }  
        return \-1;  
    }  
}

## **ESP Coordinate Transformations, Matrix Math, and Overlay Engineering**

Implementing Extra Sensory Perception (ESP) overlays requires a mix of runtime reflection, continuous process scanning, and projection math. This section details both in-process approaches for Unity and out-of-process approaches for Unreal Engine.

\+-----------------------------------------------------------------------------------------+  
|                                    WPF Overlay Context                                  |  
|                                                                                         |  
|       \+-------------------------------------------------------------------------+       |  
|       |                           Screen Bounds (X, Y)                          |       |  
|       |                                                                         |       |  
|       |       \+-------------+                                                   |       |  
|       |       |  Player 1   | \<--- Project(X\_world, Y\_world, Z\_world)           |       |  
|       |       |  \[15m\]      |                                                   |       |  
|       |       \+-------------+                                                   |       |  
|       \+-------------------------------------------------------------------------+       |  
\+-----------------------------------------------------------------------------------------+

### **Unity In-Process Projection**

Because injected payloads run directly within the game's execution thread, implementing a Unity ESP is highly efficient2. A common approach is using reflection or type mapping to resolve the active Camera.main object, and then projecting 3D entity positions onto the 2D screen space using Camera.WorldToScreenPoint5:

C\#  
using UnityEngine;

public class InternalEspComponent : MonoBehaviour  
{  
    private Camera activeCamera;

    private void Start()  
    {  
        activeCamera \= Camera.main;  
    }

    private void OnGUI()  
    {  
        if (activeCamera \== null)  
        {  
            activeCamera \= Camera.main;  
            return;  
        }

        // Locate and iterate through active player instances  
        var players \= FindObjectsByType\<MonoBehaviour\>(FindObjectsSortMode.None);  
        foreach (var player in players)  
        {  
            if (player.GetType().Name \!= "PlayerControllerB") continue;

            // Extract the position vector from the transform component  
            Vector3 worldPosition \= player.transform.position;  
            Vector3 screenPosition \= activeCamera.WorldToScreenPoint(worldPosition);

            // Unity's screen origin starts at the bottom-left; adjust coordinates for GUI drawing  
            if (screenPosition.z \> 0\)  
            {  
                float adjustedY \= Screen.height \- screenPosition.y;  
                float adjustedX \= screenPosition.x;

                GUI.color \= Color.green;  
                GUI.Label(new Rect(adjustedX, adjustedY, 200f, 25f), $"Player: \[{screenPosition.z:F1}m\]");  
            }  
        }  
    }  
}

### **Unreal Engine Host-Side Projection**

An out-of-process trainer (such as OwnWand) reads game process memory externally using ReadProcessMemory1. The trainer runs inside its own WPF process space and renders labels on a transparent overlay window positioned exactly over the game client.

#### **WPF Transparent Window Setup**

XML  
\<Window x:Class="OwnWand.Views.TransparentEspOverlay"  
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"  
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"  
        Title="ESP Overlay Window" Height="1080" Width="1920"  
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"  
        Topmost="True" ShowInTaskbar="False" Left="0" Top="0"\>  
    \<Canvas Name="EspDrawingCanvas"/\>  
\</Window\>

C\#  
using System;  
using System.Runtime.InteropServices;  
using System.Windows;  
using System.Windows.Interop;

namespace OwnWand.Views  
{  
    public partial class TransparentEspOverlay : Window  
    {  
        public TransparentEspOverlay()  
        {  
            InitializeComponent();  
        }

        protected override void OnSourceInitialized(EventArgs e)  
        {  
            base.OnSourceInitialized(e);  
            IntPtr hwnd \= new WindowInteropHelper(this).Handle;  
              
            // Set extended window styles to make the window transparent to mouse input  
            int extendedStyle \= GetWindowLong(hwnd, GWL\_EXSTYLE);  
            SetWindowLong(hwnd, GWL\_EXSTYLE, extendedStyle | WS\_EX\_TRANSPARENT | WS\_EX\_LAYERED);  
        }

        private const int GWL\_EXSTYLE \= \-20;  
        private const int WS\_EX\_TRANSPARENT \= 0x00000020;  
        private const int WS\_EX\_LAYERED \= 0x00080000;

        \[DllImport("user32.dll", SetLastError \= true)\]  
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        \[DllImport("user32.dll")\]  
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);  
    }  
}

#### **Projection Math**

To map a 3D position vector, ![][image1], from target structures to a 2D screen coordinate, ![][image2], the coordinates must be transformed through the active virtual camera's projection matrices.  
Let the camera's location vector be ![][image3], and the rotational orientation angles be Pitch (![][image4]), Yaw (![][image5]), and Roll (![][image6]). First, calculate the camera's directional basis vectors (Forward, Right, Up) from these angles:  
![][image7]  
Next, compute the relative position vector of the target actor, ![][image8]:  
![][image9]  
Using this relative vector, project the target position into the camera's local alignment coordinate space to determine its Forward, Right, and Up components:  
![][image10]  
For projection, if ![][image11], the entity is located behind the rendering camera perspective and should be skipped. If the entity is in front of the camera, calculate the 2D projection using the screen dimensions (![][image12], ![][image13]) and the camera's field of view (![][image14]).  
Let the perspective aspect ratio and horizontal scaling multiplier be:  
![][image15]  
The final screen projection coordinates are:  
![][image16]  
![][image17]

## **Technical Modding Specifications for Selected Cooperative Titles**

Developing custom hooks and trainer mechanisms for cooperative titles requires identifying target classes, methods, field structures, and assembly signature patterns. This section outlines precise technical implementation details for several popular cooperative games.

### **Lethal Company (Unity Mono)**

Lethal Company runs on a Unity Mono runtime, making it highly susceptible to standard Harmony runtime patch injections6. It manages player states and physics parameters primarily through the GameNetcodeStuff.PlayerControllerB class6.

#### **Structural Map and Signatures**

| Variable / Parameter Target | Internal Struct Value Type | Class Namespace & Target | Native Assembly Offset / Representation |
| :---- | :---- | :---- | :---- |
| **sprintMeter** \[cite: 9\] | System.Single (float) | GameNetcodeStuff.PlayerControllerB \[cite: 8\] | 0x1B8 (corresponds to current sprint energy) |
| **isExhausted** \[cite: 8\] | System.Boolean | GameNetcodeStuff.PlayerControllerB \[cite: 8\] | 0x1C0 (flag that restricts sprint and jump inputs)8 |
| **health** \[cite: 6\] | System.Int32 | GameNetcodeStuff.PlayerControllerB \[cite: 8\] | 0x1F4 (value threshold tracking local survival) |
| **movementSpeed** \[cite: 9\] | System.Single (float) | GameNetcodeStuff.PlayerControllerB \[cite: 8\] | 0x208 (base movement rate scalar) |
| **jumpForce** \[cite: 10\] | System.Single (float) | GameNetcodeStuff.PlayerControllerB \[cite: 8\] | 0x2D0 (upward impulse force scale) |

#### **Implementation Blueprint**

The following Harmony patch targets the player controller's frame-update loop to inject custom physics multipliers and resource values:

C\#  
using HarmonyLib;  
using GameNetcodeStuff;  
using UnityEngine;

\[HarmonyPatch(typeof(PlayerControllerB))\]  
public static class LethalCompanyCheats  
{  
    public static bool GodModeActive \= true;  
    public static bool UnlimitedSprint \= true;  
    public static float CustomSpeedScale \= 1.8f;  
    public static float CustomJumpScale \= 1.5f;

    \[HarmonyPatch("Update")\]  
    \[HarmonyPostfix\]  
    public static void HandleFrameUpdate(PlayerControllerB \_\_instance)  
    {  
        if (\_\_instance \== null) return;

        // IsOwner checks verify the current instance corresponds to the local client  
        if (\!\_\_instance.IsOwner || \!\_\_instance.isPlayerControlled) return;

        if (UnlimitedSprint)  
        {  
            \_\_instance.sprintMeter \= 1.0f;  
            \_\_instance.isExhausted \= false;  
        }

        // Apply custom movement scale multipliers  
        \_\_instance.movementSpeed \= 4.6f \* CustomSpeedScale;  
        \_\_instance.jumpForce \= 13.0f \* CustomJumpScale;  
    }

    \[HarmonyPatch("DamagePlayer")\]  
    \[HarmonyPrefix\]  
    public static bool HandleDamage(PlayerControllerB \_\_instance, int damageNumber, ref bool fallDamage)  
    {  
        if (GodModeActive && \_\_instance.IsOwner && \_\_instance.isPlayerControlled)  
        {  
            // Lock health to max and bypass the original damage logic  
            \_\_instance.health \= 100;  
            return false;  
        }  
        return true;  
    }  
}

### **Content Warning (Unity Mono)**

Content Warning runs on a Unity Mono runtime that uses the Player class to manage character properties11. Features like infinite oxygen, health, and speed modification can be implemented via standard prefix and postfix detours13.

#### **Structural Map and Signatures**

| Variable / Parameter Target | Internal Struct Value Type | Class Namespace & Target | Native Assembly Offset / Representation |
| :---- | :---- | :---- | :---- |
| **remainingOxygen** \[cite: 15\] | System.Single (float) | PlayerData under Player.data \[cite: 15\] | 0x40 (tracks oxygen depletion rates) |
| **stamina** \[cite: 13\] | System.Single (float) | PlayerData under Player.data \[cite: 15\] | 0x48 (sprint energy depletion threshold) |
| **health** \[cite: 15\] | System.Single (float) | PlayerData under Player.data \[cite: 15\] | 0x50 (tracks core damage thresholds) |
| **sprintSpeed** \[cite: 14\] | System.Single (float) | Player component properties12 | 0x64 (sprint acceleration multiplier) |

#### **Implementation Blueprint**

This Harmony prefix patch injects custom values into the Player class during its frame-update pipeline:

C\#  
using BepInEx;  
using HarmonyLib;  
using UnityEngine;

\[BepInPlugin("com.ownwand.cheats.contentwarning", "Content Warning Cheats", "1.0.0")\]  
public class ContentWarningCore : BaseUnityPlugin  
{  
    private void Awake()  
    {  
        var harmonyInstance \= new Harmony("com.ownwand.cheats.contentwarning");  
        harmonyInstance.PatchAll();  
    }  
}

\[HarmonyPatch(typeof(Player))\]  
public static class ContentWarningModifications  
{  
    public static bool SafeGodMode \= true;  
    public static bool InfiniteAir \= true;  
    public static bool InfiniteStamina \= true;  
    public static float SpeedScale \= 2.0f;

    \[HarmonyPatch("Update")\]  
    \[HarmonyPostfix\]  
    public static void PostframeVerification(Player \_\_instance)  
    {  
        if (\_\_instance \== null || \!\_\_instance.IsLocal) return;

        var characterData \= \_\_instance.data;  
        if (characterData \== null) return;

        if (InfiniteAir)  
        {  
            // Lock oxygen pool at max capacity (500.0f)  
            characterData.remainingOxygen \= 500.0f;  
        }

        if (InfiniteStamina)  
        {  
            // Restore max sprint pool  
            characterData.stamina \= 1.0f;  
        }

        if (SafeGodMode)  
        {  
            characterData.health \= 100.0f;  
        }  
    }  
}

### **Escape the Backrooms (Unreal Engine 4.27)**

Escape the Backrooms uses a compiled native Unreal Engine 4.27 framework16 in its shipping builds (Backrooms-Win64-Shipping.exe)4. Modifying resources like stamina, sanity, and speed requires offset-based memory overrides via a host-side tool using handle attachments1.

#### **Structural Map and Signatures**

* **GWorld Pointer AOB Signature**: 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 3B  
  \[cite: 1\]  
* **Unreal Engine Class Paths (UE 4.27)**:  
  * GWorld \-\> UGameInstance (Offset: 0x1A8)1  
  * UGameInstance \-\> LocalPlayers (Offset: 0x38) (points to a TArray of players)  
  * ULocalPlayer \-\> PlayerController (Offset: 0x30)  
  * APlayerController \-\> AcknowledgedPawn (Offset: 0x2A0)  
  * APawn \-\> CharacterMovementComponent (Offset: 0x288)  
  * ACharacter \-\> Stamina (Offset: 0x5F0)1  
  * ACharacter \-\> Sanity (Offset: 0x604)17

#### **Host-Side Memory Manipulation**

C\#  
using System;  
using System.Runtime.InteropServices;

public class EscapeBackroomsHostModifier  
{  
    private IntPtr hProcess;  
    private IntPtr moduleBase;

    public EscapeBackroomsHostModifier(IntPtr processHandle, IntPtr baseAddress)  
    {  
        this.hProcess \= processHandle;  
        this.moduleBase \= baseAddress;  
    }

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, \[Out\] byte\[\] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte\[\] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

    private IntPtr ReadPointer(IntPtr address)  
    {  
        byte\[\] buffer \= new byte\[8\];  
        ReadProcessMemory(hProcess, address, buffer, 8, out \_);  
        return (IntPtr)BitConverter.ToInt64(buffer, 0);  
    }

    public void FreezeStats()  
    {  
        // Scan virtual pages for the signature of GWorld  
        IntPtr sigAddress \= SignatureScanner.Scan(hProcess, "48 8B 1D ? ? ? ? 48 85 DB 74 3B");  
        if (sigAddress \== IntPtr.Zero) return;

        // Extract relative offset from the target instruction (RIP relative addressing)  
        byte\[\] relativeOffsetBytes \= new byte\[4\];  
        ReadProcessMemory(hProcess, sigAddress \+ 3, relativeOffsetBytes, 4, out \_);  
        int offset \= BitConverter.ToInt32(relativeOffsetBytes, 0);  
        IntPtr gWorldAddress \= (IntPtr)((long)sigAddress \+ offset \+ 7);

        IntPtr uWorld \= ReadPointer(gWorldAddress);  
        IntPtr gameInstance \= ReadPointer(uWorld \+ 0x1A8);  
        IntPtr localPlayersArray \= ReadPointer(gameInstance \+ 0x38);  
        IntPtr localPlayer \= ReadPointer(localPlayersArray);  
        IntPtr playerController \= ReadPointer(localPlayer \+ 0x30);  
        IntPtr acknowledgedPawn \= ReadPointer(playerController \+ 0x2A0);

        if (acknowledgedPawn \!= IntPtr.Zero)  
        {  
            // Freeze character stamina at its maximum float value (100.0f)  
            byte\[\] staminaBytes \= BitConverter.GetBytes(100.0f);  
            WriteProcessMemory(hProcess, acknowledgedPawn \+ 0x5F0, staminaBytes, 4, out \_);

            // Freeze character sanity at its maximum float value (100.0f)  
            byte\[\] sanityBytes \= BitConverter.GetBytes(100.0f);  
            WriteProcessMemory(hProcess, acknowledgedPawn \+ 0x604, sanityBytes, 4, out \_);  
        }  
    }  
}

### **Inside the Backrooms (Unity Mono / IL2CPP)**

Inside the Backrooms features a Unity environment that may be encountered in either managed Mono or IL2CPP formats depending on the target compile revision2. Resource allocation values reside within structural tracking classes like PlayerStats19.

#### **Structural Map and Signatures**

| Variable / Parameter Target | Internal Struct Value Type | Class Namespace & Target | Native Assembly Offset / Representation |
| :---- | :---- | :---- | :---- |
| **stamina** \[cite: 20\] | System.Single (float) | PlayerStats Component19 | 0x28 (tracks sprint stamina limits)20 |
| **health** \[cite: 20\] | System.Single (float) | PlayerStats Component19 | 0x30 (tracks health thresholds)20 |
| **sanity** \[cite: 21\] | System.Single (float) | PlayerStats Component19 | 0x38 (tracks character sanity levels)21 |

#### **Implementation Blueprint (IL2CPP Injection Support)**

When interfacing with an IL2CPP runtime, standard Mono class mapping is unavailable because the metadata is compiled into native machine code. To resolve and modify runtime class instances, the trainer must interact with the IL2CPP API functions exported by GameAssembly.dll:

C\#  
using System;  
using System.Runtime.InteropServices;

public class InsideBackroomsIL2CPPInterface  
{  
    private IntPtr hAssembly;

    \[UnmanagedFunctionPointer(CallingConvention.Cdecl)\]  
    private delegate IntPtr il2cpp\_domain\_get();

    \[UnmanagedFunctionPointer(CallingConvention.Cdecl)\]  
    private delegate IntPtr il2cpp\_thread\_attach(IntPtr domain);

    \[UnmanagedFunctionPointer(CallingConvention.Cdecl)\]  
    private delegate IntPtr il2cpp\_resolve\_icall(string signature);

    private il2cpp\_resolve\_icall resolveInternalCall;

    public InsideBackroomsIL2CPPInterface(IntPtr gameAssemblyBase)  
    {  
        hAssembly \= gameAssemblyBase;  
        InitializeInterface();  
    }

    private void InitializeInterface()  
    {  
        IntPtr pGetDomain \= GetProcAddress(hAssembly, "il2cpp\_domain\_get");  
        IntPtr pAttach \= GetProcAddress(hAssembly, "il2cpp\_thread\_attach");  
        IntPtr pResolve \= GetProcAddress(hAssembly, "il2cpp\_resolve\_icall");

        var getDomain \= (il2cpp\_domain\_get)Marshal.GetDelegateForFunctionPointer(pGetDomain, typeof(il2cpp\_domain\_get));  
        var attachThread \= (il2cpp\_thread\_attach)Marshal.GetDelegateForFunctionPointer(pAttach, typeof(il2cpp\_thread\_attach));  
        resolveInternalCall \= (il2cpp\_resolve\_icall)Marshal.GetDelegateForFunctionPointer(pResolve, typeof(il2cpp\_resolve\_icall));

        IntPtr appDomain \= getDomain();  
        attachThread(appDomain);  
    }

    public void ApplyFreeze(IntPtr playerStatsInstance)  
    {  
        if (playerStatsInstance \== IntPtr.Zero) return;

        // Writing directly to resolved offsets of the native C++ struct  
        // Lock stamina (Offset 0x28) to 100.0f  
        byte\[\] staminaVal \= BitConverter.GetBytes(100.0f);  
        Marshal.Copy(staminaVal, 0, playerStatsInstance \+ 0x28, 4);

        // Lock health (Offset 0x30) to 100.0f  
        byte\[\] healthVal \= BitConverter.GetBytes(100.0f);  
        Marshal.Copy(healthVal, 0, playerStatsInstance \+ 0x30, 4);  
    }

    \[DllImport("kernel32.dll", CharSet \= CharSet.Ansi, SetLastError \= true)\]  
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);  
}

## **Architecture Plan for Expansion Features**

Expanding the trainer's feature set beyond simple value freezes requires implementing structural modifications that work across both Unity and Unreal Engine runtimes.

       \+-------------------------------------------------------------+  
       |                  Core Feature Expansion Layer               |  
       \+-------------------------------------------------------------+  
                                      |  
         \+----------------------------+----------------------------+  
         |                                                         |  
         v                                                         v  
\+----------------------------------+             \+----------------------------------+  
|      Unity Architecture Unit     |             |      Unreal Architecture Unit     |  
\+----------------------------------+             \+----------------------------------+  
|  \- Infinite Jumps:               |             |  \- Infinite Jumps:               |  
|    Grounded Overwrite Flag       |             |    CanJump Override Ret Patch    |  
|  \- Invisibility:                 |             |  \- Invisibility:                 |  
|    Target Mask Filter Interceptor|             |    Aura Awareness Multiplier     |  
|  \- Time Scaling:                 |             |  \- Time Scaling:                 |  
|    Time.timeScale Modify         |             |    TimeDilation Variable Write   |  
\+----------------------------------+             \+----------------------------------+

### **Dynamic Assembly Injection (Unity Mono)**

* **Infinite Jumps**: Override jump limit checks or vertical velocity constraints10.  
* **Instant Acceleration**: Bypass friction coefficients and force velocity vectors to immediately match keyboard input directions17.  
* **Invisibility**: Modify state identifiers in player objects, or override enemy awareness curves to prevent AI detection1.  
* **Enemies Can't Kill**: Detour damage calculation methods, or freeze the local player's health pool to prevent death17.  
* **Custom Speed Multipliers**: Scale base walk and sprint velocities inside physics components17.  
* **Game Time Scale**: Modify the global delta time scalar via Time.timeScale22.

### **Direct Host-Side Memory Manipulation (Unreal Engine Native)**

* **Infinite Jumps**: Overwrite instruction checks in validation functions (like ACharacter::CanJump)17.  
* **Instant Acceleration**: Overwrite MaxAcceleration and deceleration values inside the character's movement component17.  
* **Invisibility**: Overwrite distance-based sight sweeps or modify the character's detection profiles1.  
* **Enemies Can't Kill**: Detour health-depletion functions, or write a minimum health value limit to prevent death17.  
* **Custom Speed Multipliers**: Write custom speed scaling variables directly into the active movement component17.  
* **Game Time Scale**: Modify TimeDilation properties inside the active level's world settings.

### **Feature Implementation Blueprint**

C\#  
public static class CustomFeatureExpansionLayer  
{  
    // Feature 1: Infinite Jumps (Unity Mono)  
    \[HarmonyPatch(typeof(PlayerControllerB), "PlayerJump")\]  
    \[HarmonyPrefix\]  
    public static void ForceGroundedOnJump(PlayerControllerB \_\_instance)  
    {  
        if (\_\_instance \!= null)  
        {  
            // Forcing grounded tracking variable to bypass exhaustion limits  
            \_\_instance.isGrounded \= true;  
        }  
    }

    // Feature 2: Time Scaling (Unity Mono)  
    public static void ScaleGameSpeed(float timeScalar)  
    {  
        // Direct modification of Unity engine frame delta calculations  
        Time.timeScale \= timeScalar;  
    }

    // Feature 3: Enemies Can't Kill / God Mode (Unreal Engine Native)  
    public static void ApplyUnrealGodMode(IntPtr hProcess, IntPtr pCharacterBase)  
    {  
        // Write standard maximum float value directly into the health offset address  
        byte\[\] maxHP \= BitConverter.GetBytes(100.0f);  
        IntPtr healthAddress \= pCharacterBase \+ 0x5F8;   
          
        VirtualProtectEx(hProcess, healthAddress, (UIntPtr)4, 0x40, out uint oldProtect);  
        WriteProcessMemory(hProcess, healthAddress, maxHP, 4, out \_);  
        VirtualProtectEx(hProcess, healthAddress, (UIntPtr)4, oldProtect, out \_);  
    }

    \[DllImport("kernel32.dll", SetLastError \= true)\]  
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte\[\] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);  
}

## **Schema-Driven MVVM Architecture for WPF Game Trainers**

To prevent UI codebloat when adding features, a professional game trainer should decouple interface components from the underlying game processes23. Using WPF with the Model-View-ViewModel (MVVM) architecture allows the UI to automatically adapt to the currently selected or active game process1.

\+------------------------------------------------------------+  
|                     Main WPF Window (View)                 |  
|                                                            |  
|  \+------------------------------------------------------+  |  
|  |             Header Area (Active Process ID)          |  |  
|  \+------------------------------------------------------+  |  
|  |  \[Dynamic Categories Control (ItemsControl)\]         |  |  
|  |                                                      |  |  
|  |  \+------------------------------------------------+  |  |  
|  |  | Category: Player Options                       |  |  |  
|  |  |  \- Speed Multiplier  \[===o===\]                 |  |  |  
|  |  |  \- God Mode          \[  Toggle \]               |  |  |  
|  |  \+------------------------------------------------+  |  |  
|  \+------------------------------------------------------+  |  
\+------------------------------------------------------------+

### **Schema Definition Model**

Defining game profiles as modular schemas (e.g., using JSON configuration structures) simplifies updating or adding target definitions:

JSON  
{  
  "GameTitle": "Escape the Backrooms",  
  "TargetProcess": "Backrooms-Win64-Shipping",  
  "Categories": \[  
    {  
      "CategoryName": "Survival Controls",  
      "Features": \[  
        {  
          "FeatureID": "GOD\_MODE",  
          "DisplayName": "God Mode (Immunity)",  
          "Type": "Toggle",  
          "DefaultValue": 0.0  
        },  
        {  
          "FeatureID": "INF\_STAMINA",  
          "DisplayName": "Infinite Stamina",  
          "Type": "Toggle",  
          "DefaultValue": 1.0  
        }  
      \]  
    },  
    {  
      "CategoryName": "Movement Modifiers",  
      "Features": \[  
        {  
          "FeatureID": "SPEED\_MULT",  
          "DisplayName": "Sprint Speed Scale",  
          "Type": "Slider",  
          "MinValue": 1.0,  
          "MaxValue": 5.0,  
          "DefaultValue": 1.0  
        }  
      \]  
    }  
  \]  
}

### **WPF Dynamic Interface Blueprint**

The trainer's WPF interface dynamically generates controls based on the active process. Instead of hardcoding UI panels for each game, the architecture binds to the loaded schema, rendering the custom options at runtime.

#### **ViewModel Class Layer**

C\#  
using System.Collections.ObjectModel;  
using System.ComponentModel;  
using System.Runtime.CompilerServices;

namespace OwnWand.ViewModels  
{  
    public class DynamicTrainerViewModel : INotifyPropertyChanged  
    {  
        private string \_gameTitle \= "Awaiting Connection...";  
        private string \_processStatus \= "Disconnected";  
        private ObservableCollection\<CategoryViewModel\> \_uiCategories \= new();

        public string GameTitle  
        {  
            get \=\> \_gameTitle;  
            set { \_gameTitle \= value; OnPropertyChanged(); }  
        }

        public string ProcessStatus  
        {  
            get \=\> \_processStatus;  
            set { \_processStatus \= value; OnPropertyChanged(); }  
        }

        public ObservableCollection\<CategoryViewModel\> UICategories  
        {  
            get \=\> \_uiCategories;  
            set { \_uiCategories \= value; OnPropertyChanged(); }  
        }

        public event PropertyChangedEventHandler PropertyChanged;  
        protected void OnPropertyChanged(\[CallerMemberName\] string name \= null)  
        {  
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));  
        }  
    }

    public class CategoryViewModel  
    {  
        public string CategoryName { get; set; }  
        public ObservableCollection\<FeatureViewModel\> Features { get; set; } \= new();  
    }

    public class FeatureViewModel : INotifyPropertyChanged  
    {  
        private float \_currentVal;  
        public string FeatureID { get; set; }  
        public string DisplayName { get; set; }  
        public string Type { get; set; } // "Toggle" or "Slider"  
        public float MinValue { get; set; }  
        public float MaxValue { get; set; }

        public float CurrentValue  
        {  
            get \=\> \_currentVal;  
            set   
            {   
                \_currentVal \= value;   
                OnPropertyChanged();  
                ApplyMemoryChange(FeatureID, \_currentVal);  
            }  
        }

        private void ApplyMemoryChange(string id, float value)  
        {  
            // Event callback routing values straight to memory patch systems  
        }

        public event PropertyChangedEventHandler PropertyChanged;  
        protected void OnPropertyChanged(\[CallerMemberName\] string name \= null)  
        {  
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));  
        }  
    }  
}

#### **Adaptive WPF Window Template (XAML)**

XML  
\<Window x:Class="OwnWand.Views.MainDashboard"  
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"  
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"  
        Title="OwnWand Modding Interface" Height="650" Width="450"  
        Background="\#121212" Foreground="\#FFFFFF"\>  
    \<Grid Margin="15"\>  
        \<Grid.RowDefinitions\>  
            \<RowDefinition Height="Auto"/\>  
            \<RowDefinition Height="\*"/\>  
        \</Grid.RowDefinitions\>

        \<\!-- Dynamic Header Area \--\>  
        \<StackPanel Grid.Row="0" Margin="0,0,0,15"\>  
            \<TextBlock Text="{Binding GameTitle}" FontSize="20" FontWeight="Bold" Foreground="\#4CAF50"/\>  
            \<TextBlock Text="{Binding ProcessStatus}" FontSize="12" Foreground="\#888888" Margin="0,2,0,0"/\>  
            \<Separator Background="\#333333" Margin="0,10,0,0"/\>  
        \</StackPanel\>

        \<\!-- Dynamic Category Generation List \--\>  
        \<ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto"\>  
            \<ItemsControl ItemsSource="{Binding UICategories}"\>  
                \<ItemsControl.ItemTemplate\>  
                    \<DataTemplate\>  
                        \<StackPanel Margin="0,0,0,20"\>  
                            \<\!-- Category Header \--\>  
                            \<TextBlock Text="{Binding CategoryName}" FontSize="14" FontWeight="Bold" Foreground="\#2196F3" Margin="0,0,0,10"/\>  
                              
                            \<\!-- Category Features Panel \--\>  
                            \<ItemsControl ItemsSource="{Binding Features}"\>  
                                \<ItemsControl.ItemTemplate\>  
                                    \<DataTemplate\>  
                                        \<Grid Margin="0,5"\>  
                                            \<Grid.ColumnDefinitions\>  
                                                \<ColumnDefinition Width="180"/\>  
                                                \<ColumnDefinition Width="\*"/\>  
                                            \</Grid.ColumnDefinitions\>  
                                              
                                            \<TextBlock Text="{Binding DisplayName}" VerticalAlignment="Center" FontSize="12" Foreground="\#DDDDDD"/\>  
                                              
                                            \<ContentControl Grid.Column="1" Content="{Binding}"\>  
                                                \<ContentControl.Style\>  
                                                    \<Style TargetType="ContentControl"\>  
                                                        \<Style.Triggers\>  
                                                            \<\!-- Render slider if feature type is "Slider" \--\>  
                                                            \<DataTrigger Binding="{Binding Type}" Value="Slider"\>  
                                                                \<Setter Property="ContentTemplate"\>  
                                                                    \<Setter.Value\>  
                                                                        \<DataTemplate\>  
                                                                            \<Grid\>  
                                                                                \<Grid.ColumnDefinitions\>  
                                                                                    \<ColumnDefinition Width="\*"/\>  
                                                                                    \<ColumnDefinition Width="35"/\>  
                                                                                \</Grid.ColumnDefinitions\>  
                                                                                \<Slider Grid.Column="0" Minimum="{Binding MinValue}" Maximum="{Binding MaxValue}" Value="{Binding CurrentValue, Mode=TwoWay}" VerticalAlignment="Center"/\>  
                                                                                \<TextBlock Grid.Column="1" Text="{Binding CurrentValue, StringFormat={}{0:0.0}}" Foreground="\#2196F3" HorizontalAlignment="Right" VerticalAlignment="Center" FontSize="11"/\>  
                                                                            \</Grid\>  
                                                                        \</DataTemplate\>  
                                                                    \</Setter.Value\>  
                                                                \</Setter\>  
                                                            \</DataTrigger\>  
                                                            \<\!-- Render toggle checkbox if feature type is "Toggle" \--\>  
                                                            \<DataTrigger Binding="{Binding Type}" Value="Toggle"\>  
                                                                \<Setter Property="ContentTemplate"\>  
                                                                    \<Setter.Value\>  
                                                                        \<DataTemplate\>  
                                                                            \<CheckBox IsChecked="{Binding CurrentValue, Converter={StaticResource FloatToBoolConverter}, Mode=TwoWay}" HorizontalAlignment="Left" VerticalAlignment="Center"/\>  
                                                                        \</DataTemplate\>  
                                                                    \</Setter.Value\>  
                                                                \</Setter\>  
                                                            \</DataTrigger\>  
                                                        \</Style.Triggers\>  
                                                    \</Style\>  
                                                \</ContentControl.Style\>  
                                            \</ContentControl\>  
                                        \</Grid\>  
                                    \</DataTemplate\>  
                                \</ItemsControl.ItemTemplate\>  
                            \</ItemsControl\>  
                        \</StackPanel\>  
                    \</DataTemplate\>  
                \</ItemsControl.ItemTemplate\>  
            \</ItemsControl\>  
        \</ScrollViewer\>  
    \</Grid\>  
\</Window\>

By decoupling game logic from UI controls, this MVVM architecture allows developers to quickly scale features across multiple game targets. When the trainer attaches to a new process (whether a Unity Mono game or native Unreal Engine binary), the WPF application simply updates the active data binding. This allows the UI to adapt dynamically without requiring complex structural changes, ensuring a highly maintainable codebase.

#### **Джерела**

1. Escape the Backrooms Trainer – Fearless Mode & Survival Boost Tool 👁️ \- GitHub, [https://github.com/Escape-the-Backrooms-Trainer](https://github.com/Escape-the-Backrooms-Trainer)  
2. Nightmare Studio NightmareStudio \- GitHub, [https://github.com/NightmareStudio](https://github.com/NightmareStudio)  
3. A databank of every UE modding tool & guide that have potential to be used across multiple UE games \- GitHub, [https://github.com/Buckminsterfullerene02/UE-Modding-Tools](https://github.com/Buckminsterfullerene02/UE-Modding-Tools)  
4. Escape the Backrooms File Directory : r/DiscordQuests \- Reddit, [https://www.reddit.com/r/DiscordQuests/comments/1txs9kk/escape\_the\_backrooms\_file\_directory/](https://www.reddit.com/r/DiscordQuests/comments/1txs9kk/escape_the_backrooms_file_directory/)  
5. UnityExplorer | Thunderstore \- The Content Warning Mod Database, [https://thunderstore.io/c/content-warning/p/CTNOriginals/UnityExplorer/](https://thunderstore.io/c/content-warning/p/CTNOriginals/UnityExplorer/)  
6. Lillious/Lethal-Company-Mod-Library \- GitHub, [https://github.com/Lillious/Lethal-Company-Mod-Library](https://github.com/Lillious/Lethal-Company-Mod-Library)  
7. Troubleshooting \- Lethal Company Modding Wiki, [https://lethal.wiki/dev/apis/csync/outdated/troubleshooting](https://lethal.wiki/dev/apis/csync/outdated/troubleshooting)  
8. Patching Code \- Lethal Company Modding Wiki, [https://lethal.wiki/dev/fundamentals/patching-code](https://lethal.wiki/dev/fundamentals/patching-code)  
9. Custom Config Syncing | Lethal Company Modding Wiki, [https://lethal.wiki/dev/intermediate/custom-config-syncing](https://lethal.wiki/dev/intermediate/custom-config-syncing)  
10. Patching Code With MonoMod — Examples | Lethal Company Modding Wiki, [https://lethal.wiki/dev/fundamentals/patching-code/monomod-examples](https://lethal.wiki/dev/fundamentals/patching-code/monomod-examples)  
11. Steam Workshop::856 MODS, [https://steamcommunity.com/sharedfiles/filedetails/?id=3434624456](https://steamcommunity.com/sharedfiles/filedetails/?id=3434624456)  
12. InputAPI | Thunderstore \- The Content Warning Mod Database, [https://thunderstore.io/c/content-warning/p/Ryokune/InputAPI/](https://thunderstore.io/c/content-warning/p/Ryokune/InputAPI/)  
13. Cheats \- Workshop \- Steam Community, [https://steamcommunity.com/sharedfiles/filedetails/?id=3656912980](https://steamcommunity.com/sharedfiles/filedetails/?id=3656912980)  
14. DXXNS/Content-Warning-Cheat: This is a small cheat written for Content Warning \- GitHub, [https://github.com/DXXNS/Content-Warning-Cheat](https://github.com/DXXNS/Content-Warning-Cheat)  
15. ConfigurableWarning | Thunderstore \- The Content Warning Mod Database, [https://thunderstore.io/c/content-warning/p/RedstoneWizard08/ConfigurableWarning/v/1.8.1/](https://thunderstore.io/c/content-warning/p/RedstoneWizard08/ConfigurableWarning/v/1.8.1/)  
16. \[BUG \- experimental-latest\] BPModLoaderMod is not able to inject pak files into Games · Issue \#1060 \- GitHub, [https://github.com/UE4SS-RE/RE-UE4SS/issues/1060](https://github.com/UE4SS-RE/RE-UE4SS/issues/1060)  
17. Escape the Backrooms Cheats & Trainers for PC \- WeMod, [https://www.wemod.com/cheats/escape-the-backrooms-trainers](https://www.wemod.com/cheats/escape-the-backrooms-trainers)  
18. NightmareStudio/Backrooms-ModMenu: a inside the backrooms mod menu that allows you to unlock all of the features of Inside The Backrooms \- GitHub, [https://github.com/NightmareStudio/Backrooms-ModMenu](https://github.com/NightmareStudio/Backrooms-ModMenu)  
19. Inside the Backrooms C\# Esp \- GitHub, [https://github.com/GGassit/Inside-the-Backrooms](https://github.com/GGassit/Inside-the-Backrooms)  
20. Guide :: How to survive in backrooms \- Steam Community, [https://steamcommunity.com/sharedfiles/filedetails/?l=french\&id=2917815716](https://steamcommunity.com/sharedfiles/filedetails/?l=french&id=2917815716)  
21. Backrooms: Escape Together Cheats: STRONG FLASHLIGHT, ENEMY FREEZE MOVEMENT | Trainer by PLITCH \- YouTube, [https://www.youtube.com/watch?v=NcUZ8fOMJv0](https://www.youtube.com/watch?v=NcUZ8fOMJv0)  
22. Tips for implementing / coding an in-game options or pause menu functionality in Unity, [https://radiator122.rssing.com/chan-5173254/all\_p13.html](https://radiator122.rssing.com/chan-5173254/all_p13.html)  
23. Eververdants/ETBSaveManager: A tool for managing Save games in EscapeTheBackrooms. \- GitHub, [https://github.com/Eververdants/ETBSaveManager](https://github.com/Eververdants/ETBSaveManager)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAM0AAAAaCAYAAAAUh9j+AAAEwklEQVR4Xu2agZHUMAxFUwMtUAMt0AIt0AIt0AElUAId0AEdXANXAOybyZ/7o5EdJ2sn7K3fjOc2cdayZEmWs7csk8lkMplMJpP9fLy1z0n75A9NrudDvDEZBs5fsjcB88s+/721r+v191v7sn6eXIwWZXIO2PvPkgfOt+VtR+E5gobgAYKm227z49Z+b7Sfy3SODDLXS7y5Em1IcxvGPtahJ2TcKAOncpjPyDkAvhPnEVsWADUIAL4X8Z0Eub42lGjdwFAMTlSquULe1y1S3wFksNelvBjcZ3F5BtvxV1kPcGDu49xds+AK8pHB+tEIEJcPXLPGWvPecyAY5DvsDuipMwYOrr4joBPjlaCfwBkGwj1oIgRWtvDPjDLoFm47BweN90ag4MyyOfdwZgJqBIyL7Gx85CppHEFjZ8TzzBC2ggbU1+Io752aI2YomwPfIViGLmgA2TipQzAfddhW0DPaiGSj+cS+vZR2kliaDWEGzT6wQXTCGsqKlCUjM3sJrZ1XCT2cdousfGIeBFOPUhA7ZuMML81gK2iYmPoyQzwb2GHvouhsEw/jZ6C1k2yCd3TAZODkvQIGdG4TjKtdncDh87AEVQsaPyyekZ16g2572taZTQlkb/LQ2eYK+1GGaf2Y//DSJUF26/1bCXa9hBg0Wcvq0xEgQ29XMgfWL76txKDYaplMB9lHgka1/LDMV0Hlodaxt+NuoZceyO/NZceFGDR7nHIEbLm1M8Nl2WV5C5pWG+Gw2qFl317lSSt6m0Q7I/EJvZ1DbixnsUGPFxEzaFYwZi0wagE1GgVNy47hAQPKttGBRqOd5my7KWAyx8YGPc532dincDRo2OY9a3KNg+DwMaOp/PGxVQ5hPD67c8USgmcYV07biuvV0rZ0by3PVJK4fXSuKZW66Ozjyp5qR1FpWEpEzNHlcq1537MrItOThtAPwyqFpbfLks6ZLzk9dqtDHAkansGRFekqP1QK+BhSnPay9tH4Ltd66yHD8X03FIZXZue5M34ULCE9t4JGejleJsWdSvbUuG5PxsnWBIeJMjKYC2PFRATIIah8fLd3pqfWqgZrjtzo8Fwzvu960tt9SXpHX3J4ZmseQ8A4TNaDpuV/zbS1KnuxIBhJ96Qo993JkUU/3+d55GjhtOvEMsKzCd+9LLusoE+pLFDWzvrRTw6Mjv7SQfaUbm5PiI4jh2op9bSu2UsOxmUsyfdghcwpa+MBa0o/YzK+N+kveQK93ZeE+1KE+1ki+K/xwMIJZAgpD55V4y7kTiFUmmlBeFaftaAt2XUkcore82Bc2cTtWSMLzr1oXaA1KRFMJWfei+sNnjTdlyIxuT4EvqgeDG5MjKFsgAE8M2ZZknG084A7JvKuLM0c9Kot6BHQT0Hg9ixlU+5nNtyLdi1AvpeIJeJOeQ+uN2gu6FcKTA/0h4KJKxD46wYXOq+QvaKT+U4lCAqec0fR+IyNs/ZwlHth7qUD/VFUvkV7RrsJ+ktOtRfGQg6NgEBuyc7I7LHDCentvlTTG3iu5xxOhaCQ4/D33pJFh0CHa5cR+69CjtabVnuWnPoobtdaMBJUtXkdxX2pNj598c3k5EHQm6Bn40pnlc2zKmUymUwmk8lk8jz8A4aVwSb1TBhWAAAAAElFTkSuQmCC>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAKcAAAAaCAYAAADSQkxHAAADz0lEQVR4Xu2ZjZHUMAxGtwZaoIZrgRZo4VqgheuAEijhOqADOqABCoB93H6DEJJjJ95swujNeJI4jn/kT7K9e7kURVEURfEY3l/ThyQ9mXLFQWBi/icQYAT5r+b55zU9m+cXc18cgC/X9MlnnpzvlzgKMk6bjzitkDeJE5V/XUh4Bp14d/umyPl8+TuSWIim3rYkJpfk85/fPlsNwvB14jiWqN0I+v7j8q8GPrpnRGzZtIJgADqE4pW+3fJI3CufzhU5TFQ0gYKIQhlrZ+xPeRLCkZ25z5bSXhCenV8f5YB2cSjeI6xWpKNPmXiFF/8UrDi92q1BvacUf1iaXCGH9xONeHiXiXstCLI1dwiqR1Q4C/Ug+AjeZ+820RIn2OhaxCCsHhAf0RFbErWAZx/VZqLo6IWPQ/m8FoiPeqK+9gh8FSXO7UhoPdglXHvOe6LoaSPbmnYVPaMDn99vTmNJnPJ0vxQVb2CzkSVNYtnT4WlHhzVF7zXYeuyBSvoYsUMXmTjthpk06mlHg/6zL+xNvYYmkkRO3UJ7z97twFY0h0Q/HcbWgAD36vNvrDizlP1EcibuJU7KjogTp7e/hOyB2iJijmxBPIqSu2FFiKGLMUZsZk/H2nvuAXtC2spO7b2UOE8GNuv9XdKeaqODyr3Q/I6cziNOJU6WCL6xg8bYPl8TyP7M5nMlj/L+FKh6VBav55lJVf29ogDqs2NdSkxED73LOuPz4mB5b7Vjx7oFxrO0V9S2p3W2oK9rD1OrWCtOickaT2IDLWFMHINiadEJT0YgTxOrvZD2ZKpHYuZK0s8W3L/e7h8JTrMkThwjEoccJnIy2YqxR8sx30b5EbQh+0aoLeprOQu2b72fBgZlgn206BWoDIeI5G3WAAiHNnjPoDAm5fX3HB4oAfKsQXOV6DQx1EMbfKt39HMXQy2giBNBv7W3jMrwnneU8VFVzqe/Pj3YT47aYunfHVBbtBO1JZZEfggkFsRFh3mWoSMiI0ZlVUcUiWRkrva+tQztxeylToLUNat/hnPatmgnciJAlFk/DoV+ksDb1WEJRvAsb43+3vLilCCpz4pTdXCVyHEKLZNH8GTG0oo4oxDF7DYg2hJA5PSj2La4Zs5OW5lwDwViQxRaCgSiYQC8014RoiVFe1KV51ugrLYXcgDgXiKnTYzFc2bMPaEfM6KYwAbYFhvIRhFZ/gi2raw+yhA0/NbjsESbeIiW5IxsWcYIvh7KWuP450eiFWRm9IxsYMFus8bfaot3BIIowBQnQb8+ZE47m8ip74FWuKIoiqIoihF+AU2nZrn0l8vRAAAAAElFTkSuQmCC>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAALcAAAAaCAYAAAD17M2vAAAEhUlEQVR4Xu2ajZHUOBBGJwZSIIZLgRRIgRRI4TK4EAiBDMiADEiAALh9tfugS9X6m5E9O4telWo9Y49a3f2pJdt7uWw2m81ms9nM8KHR3oXrNgvYAV0PQq3xTzj+8tR+hM/fw/HmRhD2TECz6lJ+PgIrW+R98fkIsBHFOAqipfVA2PG6r+F4GTjx+al9e2q/XtrPy7MxA8vnMxJ5Jggbv1t8ujxfR0yMDwkhFrSjREbf/16eBUDsGQN/+Q5mJuUM+IN/2MJX7NM+vpwnHiMQq5IYK47pP/ZXTuCbIID/Xf4ImkbwbIg7nltq/M7gd5YAoWKRVPzm2nI5JTZHCYyEIy5anHyuNIxnpDLOohawwRgsZvjuxOL8CIi3pRf6p6+jisPviqRDmSEG+NbEbdVo+WNMasuyVXw1iJZ+mTzZSsl3CG20go5Cvxa3jKiDUSgONcr99lLiYHuBsoLVEv1oIA6qVIZVqiV8MH4rsWJnoo7UBHgtTqgeTOiZFQPdZPvobEuyFB2i9YLpcvUWUJTuIUtGKzL9rKw8MxV5pbhnKjKxGRmfuHcvOXxLYjUecaq3f3ok3DdmgZ1JNL+fSXQPx9UrNLAyF05m/vbA3yxuNfQprvjYUXscj9idxiQe0vki4s3tSBsRmzfJGZ5bWZFH8enEmSA6dTASu1l6q+Rh/K3itlJlKLCZfeUKosjOhCcx2p2pyKMobnJzKvExX28pJPiIonfdI9BaCo1Hb9mnEtVuSK9hZjuE3VWVENGNFjieHM3m/27ipsoZ0F6wnOFvAfd8GcajtwLQR28CzDAqbsTF2GdFVkNx1+IhjG9kApTcTdxgQHsDx/l4DWInwIiAShITzfJW2yZY8UaT4/hGW88PaG1LXM1aVTlLNPHgN6xw+HjNI9ORPbdxBWKITWxLPB4hbodaOckmsz7zu9qWRnHPjmsJBpTmK+UMrjNhDBjR+oLDR1g+PSAQOotYxGDAXWbyC4yhJiKTkT2+At9axmQqauOAb6X4ge9aK6RVtIYxF/PFbxRePC/8rmXXyV7LCXbKyc615JaGzZjnCLbj+E6F4OgcjcQxEBuD47tYiZyFsSrjIA6XLz9iwLDDZ2xeU9lWwfhbAVf8WTUqYwHGwRcTtUeExrcFccyKDHEr97zYsPL6fXYjTE5adhlvbdXgXNanscEu8SzHK07Y2vnTUdhlEiOxEnBsUstAGFT7oxGMmrDOgnHVqo3gl9sAjmsJMtH4rv/ZxICsopc4ObTdihV+xMl1i10gN9jkbyv/xCJOwppddGExfBjiMmVFLr9HEC6T8XV3TSRn4nK5Yiz2E98uZpWba1oV9BriCtQS0Wq75BUf8Qlh1wrFyL8SvDpwDrHiVLb14BzHcbnkO/dqrwESXttnzuBWgr7iRC+xEq/EHPC3JeDVdl3VmFzxfqxktd3DQbBuQ7JZ6c1VCdfXgnAPqHjeCN9CjENra1DumW9FW7bWTfBKu5FWPllJjrJ7GCxHrSrxSOCLW6ejWZ1oVglXQVeNjJYAj8KnSpvNZrPZbDZ/I/8DCOyLhPhmlpQAAAAASUVORK5CYII=>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAaCAYAAACO5M0mAAAAqklEQVR4Xu2RURGEMAxEowELpwELWMDCWcACDpCABBzgAAcYOAFHHhAmCf3j5z5uZ3am2W67SSvyk2iUVRY93spVOSs/ae8CJjZrVxdv/So7VxOPOYCTRHpg7JO2R76ShikYWzliMyZJxlEOo91gJIX4C/SWjXY4TI2Qo5k+aFyNwAMbGAotvIKJg9OIRru9IU2bkZ6omfgGGl/ONQbWxa9DZBNiLJr+eI4NMXAskXfH5PgAAAAASUVORK5CYII=>

[image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAbCAYAAABIpm7EAAAAqElEQVR4XuWSURHCQAxE46UasICFWsACFnCABCTgoA5wgAEElHvDhNnbMjm+4c1k2m5yd8n2Iv6DnQsVFC8uVhxbnFysuLbYu1jxcKHiq/5v8Sqa5XlocdaihASRaDtr2Cwcj6jf2g45DHjDhy5wO8l18yDQf3KJvgXy3Ry+w13egfykAgV5AgldzEkf/3ZamcEmzFZePnbfWFjhdg5xO4e4nUP0avwsT+dOKZTPZazGAAAAAElFTkSuQmCC>

[image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA4AAAAbCAYAAABMU775AAAAs0lEQVR4Xu2RbQ3CUAxFnwYsTAMWsIAFLGABB0hAwhzgYA5mYAKgh7y3dJcmK/tJdpKbrd36XcpO46iODAT16sxwrfoZqm1qdVRHhtX57qaH6Wy6OL9v82aa3LfSmZ71nSC/CN/moUggmVoVKpAIojZbgQVkfDk7OgNFvqCqD4zOoPYHluMD9QwnsWd8oM6HrYkWcA5+QCQa6pP5V2GrrJ3WUgENbTNNdIYU0RlSbKr2j7wB7pIlLtwRBCsAAAAASUVORK5CYII=>

[image7]: <data:image/png;base64,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>

[image8]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACUAAAAZCAYAAAC2JufVAAABKElEQVR4Xu2WYRGCQBCFL4MVzGAFK1jBClawgRGMYAMb2MACBlC+gTc8d2DAg8M/fDM3nncHLG/3raa0srLyP87VePeMvZ1jHve5tgjbVD/wkNqHXZu1jZ1jztqlGsdmzrXFcRUIMiK1FuWZ2qDuYQ9Q8BUXS3NK32p5epizRvoWhbpBCa8tIUMsUkcRAnG1BMHe7PviUE8e1KMZcyHVu8zUS2wPfNIG5mKX6nt6yxmFO3Fux2GoLOVRRkENdW7eHFfSxzjradEa+5wDajPLxe7EIcfpF0Dd3pVlj3sx1PvY/6meImPz7u1DkCICQSmvyazU5UANRrylOCgq9Yqhbh/xvkYAFDiQOgVWDB7SpRSFTeoYpFe1yXzIPJPhjfvMEP8CgVy4MokPPFRbT/uPQlYAAAAASUVORK5CYII=>

[image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAmwAAAAvCAYAAABexpbOAAACQ0lEQVR4Xu3c4Y3TQBAGUNdAC9RAC7RAC7RAC9cBJVACHdABHdAABcB94BGrkR3kyybng/ekVeyNL5n416fZ9S0LAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAC/fHkcP9aR4/7e9435W+s1jecfh+vO6NXyp9aHddT52+E6AIBDPi2/A8WbjfkEkOdQIWeUsJa5123+TFLftz65CGwAwARbAamf39NWPdWtOmvwqeC7FXLP3hkEAF6AdIXGgPR+HTPU0uDW2AtfPbCllj53NmevDwB44Wrv1df1/GjwOHr931T4GYPdjKXQ7Ier31j2as937o0t2e+391kAAFNUSEqgyfLeET0EXetW3aqEqncbczNUF7B/fnzoEwAAT5HO0V5Qyh6sBJvai1UBLd2v2Aop19iro9R+sTr+vB7nwYn8jgpIOa7aYwyWOR6vnaG6bH0fW31/asnycyTgjfXW07h5nX0/AYB/SMJGBYqu9rQljCTsVFibGXiiwlqNvSXIhLTUkqXSWi6tEJfAk/d6p7CCUAXPWwSjMfhm9BrqPo6hM3XUcV5ndywBgP9AfwChQlTmx87bPaWGCo2981ehc+zSJVgmxOXa6sjlutmB85IKiFX3WG+6bVWXwAYAHNY3/Pclv+dQNfT/HzfWeqnu/nf3kCDW712vca+jCADAHVzalwcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwHV+AiF6iqWq+ykwAAAAAElFTkSuQmCC>

[image10]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAmwAAAAwCAYAAACsRiaAAAAFIElEQVR4Xu3cjZHjRBAGUMdACoRwRQqkQAqkcCmQASEQAhmQARlcAgQA/mqvqa5m5L+VvMvee1Uqy1pLM5qWZtoj351OAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMC78cN5+XJe/jwv37ftv5+Xv8/LL23bnv44vRw/S9bLz+flr6/bss790m5p18+n/8Z0xnkvv55ejp1ycz312CWWFdNv3Xfn5bfTSzv91LZXzI665tP2dc8lFolX/9u8DwF4h9J5z846nfoRA3vXE4su23idVRsmpkerJLxLgpJEhRdpi9lGR30xmlbxyX3Yk0cA3ql01r0T//H0vAF2DiApeyZwe8rxs2y9/yjmoPysmNZsTS9r1mVPKafHMDPGWc/rezbb5MhrvktimLJ7gjbrsqeKTV0PH/V+A3iKdKC9084jrWeZCVse2x2ZWMzy5vuPIufUZ0ifFdNK/vuM0Zy93VNduxXD/8vjvX7NHfUYdKVm93r7HDnzWrGpJO2j3m8AT1OdaAb5njDVjEUtq8ek+XwG6K3lkhw/ZeexWdzbmW99W99K+mZyGvN9XKv3Xrbq/1o5p5pFmTHt8dwyYziXS/qgnN+13eNSvbZimvr0GM6E5FYp91kzc72+1Z79HsuS9UvnvLVstV/p8cnPIVb39JZLx96qa0/YZqwAuNOlAbZvu9RhP6oGkEd+S7M1O9AHie6WhC3J47NmPWbZe0nCUj8snzH90ta32u816kf1q7a+JgnTauDPI8OtY80kIOtbCdvqmiirv82220vqmPOcx+/J8L33wq3qt6Oz3W6x9flL8en34iNlAtCkE+3/cqxkAM3AsRpES76hZ/+t5ZrqxFefTZ3S2dfglfV8vmZC5oB3zSqJ6O9Xj/SyXglc6pP1Kr9/br7vsx1zv7xmkMsj4COk7J60dXW+idusf5kxnMs19bk5e5PznjFNu1Q9HmmPmQRkvRK2WVZvj2xPDKqO2Sfv67M57iP1uUXqmPPu7bMVi5UZj77ccpz67PxiUvf7vI7rul1dT9eknFXClnLquD0+6WseKQfgm5BOdPWNPgNWOts+K3OElD876XTcNaBkIO2DSH12VedLKrmsBDTnNcvuSWDNQFXyVY9ua7Cvv9f+tW8N9NV+fb/UoWaS5iO4GkhfqxLTVfukLvn71izUHqpdp8Szx7Tq8Ogj8agZo8gxs564fPr6t9re41WPHRODevxYx6jXmczEI/VbyXnPLxv92Kuy91SzoFPKTeyq/H59b81+XpNycrzsm+Plfd3Ldd9UXSoeea0kD4Bm61v5qlM/wkxcSk8UZ10yAD8qg8HW4NPbohKJqsesQ/2XFT1hqKSsJ2R9v0pSMlit6rDXQLU6dg2cMc9lb3N2rVyK6Vab3KLarV9Lvayo489EMe97EpHPzH33tIpxb4tK+I+0dc/1ZLHfC6nTo7Gpa6Hv38upNq8Erl4BuCKdeQaxDCJv2Xn2QTN1ygCSpR7n1YC7lzmLkDboScQsL/XLtgxmvZ2yrdev71fnkW3zeEcmCplt6/+BbWI7yz9a6tDPL4N2xTT2rs9sy4pRrp+sV5LUf8tXdcm+89rf+mKzh7RFlorPW5gzspkFrPutJ7V7qHs4bdxnWLNt1gMA/lWDx1tazbywv/6o+x6PzjBxm70TdgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACafwApjGywr1YSLQAAAABJRU5ErkJggg==>

[image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAE4AAAAaCAYAAAAZtWr8AAAB2klEQVR4Xu2YAU0FMQyGpwELaMACFrCABSzgAAlIwAEOcIABBMB9OX7S12zv2ruFvEC/pCF5t63rv3ZdaK0oiuLv87jYq7OnkxGtPZhvGHMumRv/Q4Dbb7v2H0bg5H6xz8Xe2iqSn8yYl7aOQTT//VKw+4xC7B+LPbc1tvfFrk5GbIAznI5Q1l0iHDj7J2j+ZoRjLLEJREvFyQKjCSzGqfx2pt212OnbEssKR1we5rNmiJFDFkin707wQbkcOaRRHD0o016y6NoKIYd+w4gWVn8nEgxfR+/QjHBqjJ7MGj+DrUgsfO7emwEikWH4mpHVmaCnCse9InolSudS6/aWyRTG0sl6Po6QCXqKcATAYPtGs91GqLtqLMa7T1mzBQJLMO6Y2WSCniKc3j8spC46gm/+kcwmonehzbbZ4mWCHj2xmM/eQhC4hEMUL4ygVG1Jq8yiolkk4Kz7DTLCEctIuPDdjvpyeu7e0TiEHaV6FttRR36jnBOOvfoD7lUW88OVwIJyahuEh5PgjaOGEE7pDeyThCzcg42hhyrK/2bjZR/hNxyoBLdS1N9ve4McwcY5bdaNdmmJ5a0nkm94amyqIETb80+CTXB+tJz+FbpIdZKj5lEURVEURRHkCzV1q9cvQoRCAAAAAElFTkSuQmCC>

[image12]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADkAAAAaCAYAAAANIPQdAAAB4klEQVR4Xu2XAU3EQBBFVwMW0IAFLGABC1jAARKQgAMc4AADJwD60r7kZ7I9uIaGXrIv2VzZmW7nz8xuS2uDweCIPE/jvYzHxVbnGXJzxnY47qbxNI2vaby1WeDtYnuYxsdi+2xzQgSR+GJ/abPv4UEIYisIw7ZWqdc6cWQQkpUSRKyJpAt684elJxIBVpKR0J5XVUXoVYu/7xdbFcledO9eDT2RiEiRHDZAdWvVr4IqkpMTEKpIBCOUKir4L2GP84y1k9qkG9vFIILXBCDAa22KZB9ufkibBeTaCeti41UGxJGCsVOI2nG/RiFkqwahjVcMQvcCUTyHigIJ5R2cmOxNKIQs1kX8IOC3B0GxRzPrBsq810DQtdXxYZ5KnWLOqrr/PR/4xf/ic0GRvVZgTnsPMm4AYksTtMH4CZnBkQAGwvG1VblfQZkwfPxoqVX+kWzXiiLXMuenoZUm4Pw09D58CDIrnluD+xWAyNo5xJHCNolcu4nsElxtsyQDrIdFtn+Kws/qb9mP9ezYFQLLVrXlCTyrT3UZ7kuSoi+JZOQ/DOkPuZWYw78mYlcIwIcyEEc1avWxuV995+KHP9es4SvMA8k1EC8mqK6/O2Ye6gktvT0v2DLoPJV7nFtrMBgM/odvqdGhOm6IKmAAAAAASUVORK5CYII=>

[image13]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADsAAAAaCAYAAAAJ1SQgAAABu0lEQVR4Xu2WDU0EMRBGVwMW0IAFLGABC1jAARKQgAMc4AADCIB9uX3J5Et7t5fcJuymL2my7bTTaeenO02DwWCwEz7n9hvtZ5E9NGS0l0W+Wzz0awpmHqeT7C4Fe0Wv4c0ET3IZhwBvctCPFCwQ1nj3EBjCrVw0bw9DLTx4sLa3RXYILD6EKh7OxnjrsBQr1m5VtNT/vHzfhDX52joshrzn4ArYr7dXRf2tveXqOnKpCiPrVeLe+K1Af+sphPvp/EU0MYRb4AHkvQ2Vkdd5Wdx6yvjOIkiIMua86i30E8bqAebTx+tXvxJrQrilsP5oPM3tu8gwhIaMZgTwnfq+ptN6YJ561M8h3QPMZea6x2p6nnOzXqiwBkMhD1sjRa9hlM9YNbDmPfPsV/3gYYX9cqzJpX/eHG8duOaTnhQMUU4YSs4j76rBrHN+5muuy0vbDL0OGKsnq1fEnAS97zyMFtZkCIt7uK5eWtWxCfX5IEowkjFzkfCjbyHRIObRr8UMPYyxpl5WrSPuoX68n57fDAtPrw8YlmMcOseEwxrCLX3ZP6frX2IBwnOE6a6MvxZCkwpMSOY7PRgMTvwBjZKgDZ8t3yoAAAAASUVORK5CYII=>

[image14]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAC4AAAAaCAYAAADIUm6MAAABmUlEQVR4Xu2V203FMBBEUwMtUAMt0AIt0AIt0AF//FICHdABHdAABYCPyEhzR97EiYQQko+0ghtv1uN9OMsymfwZ181um93kwm9y1+xt0BDoPDb7aPa+/KzzP8+u3GldS8tD8k765H4XkCk2w/Gr2ef62+1lXfNAD81e4xnwPgdx8eyhGNiTrQn8eVfr95fLNQrM3x6sCfn24EC9OIiX8My2oGL4HYKXCFqdlGyARFOJHmROAr0a/rwSl4fdBbEERLwjsfC8/t46nKBVeoerngOtephem2hoBZvh44epUGZToOYoM0siTglXmzAUGlYNafrkpgn9K+FZGeLz3EVqKKu+L6EPtZGuIYn0XqzEJBpOLMWoarSM4DBZmSHU394CugEciakGS6gdcl5Aeyk2sfDLe38IZZdMCaqQ2dKGeW8n+FT96m1ElvkOnOaIoL2Ms0bltmIpDgnb8tulKmvSu3kcsjkyZLpSvcKH0UfBh6XCP8lclQ6/9anfQzNwCu+1tK1bg9L6XUyfIpaKjWaQvj4tfDKZTCb/l2/1Uqw3sKzK7AAAAABJRU5ErkJggg==>

[image15]: <data:image/png;base64,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>

[image16]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAmwAAABBCAYAAABsOPjkAAAFIklEQVR4Xu3dDY3jVhQG0GAohWIohVIohVIohTIohEJYBmVQBiWwANp82r3q1dNLMs7YiZ2cI1nZ2Pmx34zkb+999pxOAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKznz3HFi3u34wWAt/DjefnnvPx9Xn46Lz+cly/n5d/z8kt7XdZlyfaj+OO0n/39/fRtTPNYS56vLcf767gSADi+BJu/2vOc8McwkUB3JAmiCUV7kTFNMO76mK9pT0EVAFhJgk0PD2P1J+HnaMbA+WwJa1X5+u37YyqWW9nb8QMAn/Tz6f8TfM2B6if8rSpBW0kw2ltbMOOZcU5Ye0SYSujO9wEAL6IHtjrJ1/N7g08+Z1aZ6/PitvKIQLRU36d7W7VLA9gexwEA+ISc3Pscqz5B/iMyb6rrwaz/e+sQkblb41yxZ0vo/TquvMPSFurWYw0APFhO7n2iep7PqmsV4LItV5VGHnsoS3jLRQr5vLRYe+jLtjHcrSnfN9vvZ+rz10ZpkdactugXDOQxY1fj3APY+L6ZvHfLsQYAHmy8CnRWnUlAqEpRv99XAkW9v7bXY692paWXEJJl1i5dw2y/nyn7U8us8pegW6Gq5gpW67MCWT3W62psb80tzDjvbTwA4KFyUq2rKfuJtVqJrypBLUEgIa1CV92/LcYrIavSFhUwvrR1a7sUUGqe3vjdCUz9Z/hIGavar3HOX/0OVeWyj/GlY5x51rEBwK701uGsgvJqcowJbb31mEBQFbeEslSCetWogkmvvl1qEc7cqiR118JMwuMYppdO5F9Tjqu3PBMmM25Vhaw5hFmf/awxrjbzR4JYxmPJ+AHAS0rwyElxbCmyniWB41pgSxjqn1VhaInxTz/1SuMeVTsWAN6ettO21gpsCWi9CppK39LW7BjY9l5VFdgA4LveFn2Wap9dWo7so4Gt5oNd0reP94PLtlpuqWrq3sNaCGwAcPp28q62KOtImEpIqyWVsP685seNbgW2qO2zz6h1s22jW9+zFwIbAG8tc5f6zVBzUhzba2mT9lZpTShPxav/cfKsu/TausdZXnut+lMn5kvLka1VYYtsn803TOiun9+tm9zObm2yV6/w8weAuyQ8jSEi7bExCFy7x1YCQt0io4JavSYT4bM+78/rqvX2rifecayvuTVG2T67SCBj3K/EvGRsg956/bMJbABwRYJWVXzy2ENCtpVxftlsHlW9vleB9iDHNO7rFtYMbJcuDqn3VfVzawnl1eLtvw+1bskxX5PjWuuzAODl5CSZk//sHltjJW68r1beW6+tyfEJER+ZW/UoVVnKPiYU7CVI3gpsMxnvanPm55Axf8TxZF/HAJnfkzXlO8b/FAAAbyJBoCpDa1aEPuuewPYsGbOxpbp2xfJSCxgAeAO9ApWK4Rg8niUhaLxlx16l8tXnxN262GGpBOq1K3YAwEHtqarV/xD93vWrWseQWXMZa7mnRZswqLoGAOwqrJU97tMlta+z+YmzdUscaRwAgI30dt5e5rBF2rN1scfeJVTNKoK5GKFfdLJU3bsPAHhjCRkJG7WMVzs+21GqS5cuCsh8trRCx/u+fdRRjh8AeGMJkGtfcbmFWdDNus8Ertm9/AAAdmnWaty7XNmZsJYKW7+h7hJHPG4A4I3d21I8qrVvDQIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAO/gPxWQMxtNFA/bAAAAAElFTkSuQmCC>

[image17]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAmwAAABBCAYAAABsOPjkAAAFWElEQVR4Xu3cjW3rNhQG0MzQFTpDV+gKXaErdIVu0BE6QjfoBt2gC7wB2nzIuwBxQcn5sWXKPgcQHCuyQ1EB+OGS0ssLAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANv+fd1+6jv5sD/7DgDgWD+/bv+9bn+9vIWbH163X7/v+2U47my+9R139PvLW3/mtbaEyTMFobQXALijhInRby/nHqD/eHkLnqtIAJ71Z/r5TGbnAAAcpAe2VKcSMs5qtWCR9lR/jiHtx+Hna6oq6ai/797TlrQ9FVkA4GA1/fn3sPUAdyarhbVIfyboJPAc1bdjaMvrXsWxpo9rejy2+vGo9gMAg7H6U8ZBOevaPruWbVbpubUVA8XYpqxf+4yEqfdUwUa5dlkntxfWen/l+PytretWax0BgINUdW00W792aRpsL9ClYjfq331NWbu2FTTuJe25xg0Qn1nvlvCVkLfXJ/361w0SWxL+rnE+AMA7JTz1wbmvX8sAnvdjYMj7qhQlJI1y3FiBybF1TD6T778UAD+rn8sKZhXM0XiDRPql+ir70l/Vl2NIGvt/yzgNulfp7H2WCtql7+6fAQBuJIPuuPV9FRTqd/98f62KWU211aNAal+MAS3GCtxeNe6rVgsSY3/OKovpy+rD9EuFtKiAXK+z/t+S7+nToP19yd/Ld+X7x3C4V9FbrZ8BYCqVkAzA2cZ1RRlMsz3SGp8KCBnQc659sM7gnv3jgvX+WsdUlWcrPHxVb1vJ9ci1qnMoqSb1fUdKuxKMxr6LvaC7dY5Hyv/EXqADgGXMwste1eOsKnzWudV0XIWKBKE6pqo1dWwCUeSYhJL3TOV9VoXoLan6Vfgs916LlTZVH6X91X9VacuW3+d9VS17/99D/nbvSwBYVgJbVWdqyupZ5fxTdRmnSI90KUSkQjUG7ASkj7Qzx86u8Wzfo+sVQQBYWgatmtL6yOD/iFLdSnVoLzTdUlWntvSQsVeN29IX7+fnZ7zuvS8BYGkZsDNw3XIh/SUJjDVFNtueRYLipfOtkJGqaAWthI9s/ectqSDmuj9rWAuBDYBTma1j42sSiBK+trYtHwls4zTmpc/MZF3ZMy+6F9gAOJVMwdVi8FH21UL8hLq8TxUuW/aPISG/G6s99X39dUum9jJ4bm3P4r2Brffn2EfjdOeWul59evSZCGwAnEoGrf5IiISG7Es4q8dd5OeEu4S1MeSlUpOBP7+vsFGVm3y2Qt+laTou33QQuV59+noMHrkee/o06LOGNoENgNOrStosQCSEVcDr66X6AFgL6OsxDytJe1YLkenz3t/drAI37lvtnKLOq08J1/tLIfMWZv/bAHAqGUArbCXYZHDL+3G6s2TQqym2miqtz1aAy3qr/rl7GqtMaeMqDwlOm3rofY/0b85ppT7u8n/Rnxl3r8enRP5vZ+EXAFhEQlGFygzanwlJt7JSW65pNgV5zwrXbCkAALCoVKVy08Mqeqh5JOO57T1v7giP3M8A8HBWG7gzHd1vKngUY1+Pa+1qLWRtR1S+VrvuAMCGvqZqFau266sqJPWKZkLq+Fy5W984karqM94ZCwCnMwaE1aotq7XnWnJes+ph7T/qBoRH7V8AeChZP5VBu7Z7PFZiT6YE773G6xbS17M7WROeU1XrlbdbyN9xdygAcBVHhJejzaYh82iPWdXtVlTXAICretS1bCVhLedYz/K7tUesWgIACxAyriNToas8IBkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAO7tf/ESOrW5LEKaAAAAAElFTkSuQmCC>