# OwnWand — Developer Guide & AI Prompt

This guide contains:
1. **GitHub Repository Link** for the project.
2. **A Premium AI Prompt** that you can copy-paste into any advanced AI (like Gemini, Claude, or GPT) along with the code or GitHub link to get step-by-step guidance on connection issues, adding advanced features, and more.
3. **Wand/Went Features Reference** detailing all popular capabilities (God Mode, Invisibility, ESP, Speed, etc.).
4. **Technical Explanation of ESP (Extra Sensory Perception)** for Unity and Unreal Engine games.

---

## 🚀 GitHub Repository

Your project is now hosted at:
👉 **[https://github.com/OlehZvenyhorodskiy/ownwand](https://github.com/OlehZvenyhorodskiy/ownwand)**

---

## 🤖 Copy-Paste AI Prompt for Code Review

Copy and paste the text block below into an AI model (like Gemini, Claude, or GPT) to analyze your GitHub repository and diagnose any issues:

```text
You are an expert game modding developer and reverse engineer specializing in C#/.NET and native memory patching (Unity Mono, IL2CPP, and Unreal Engine).

Please review the codebase of my game trainer project called "OwnWand", which is hosted on GitHub at:
https://github.com/OlehZvenyhorodskiy/ownwand

This is a WPF application (.NET 8) designed to apply cheats/mods to Unity and Unreal Engine games. We recently added a detailed logger that writes step-by-step execution details to "ownwand.log" in the root of the project directory.

Please review the repository (specifically src/OwnWand.App/ViewModels/CheatPanelViewModel.cs and src/OwnWand.Injector/MemoryScanner.cs) along with the committed "ownwand.log" file, and address the following:

1. Look at "ownwand.log". Why are the memory patches and pattern scanning failing for "Escape the Backrooms" (running as "Backrooms-Win64-Shipping.exe")?
2. The GWorld pointer signature used is "48 8B 1D ? ? ? ? 48 85 DB 74 3B". Analyze whether RIP-relative address calculation in MemoryScanner.GetGWorldAddress is correct and if the Unreal Engine pointer chain traversal (GWorld -> GameInstance -> LocalPlayers -> PlayerController -> AcknowledgedPawn -> Stamina/Sanity offsets) is failing.
3. How can we optimize or correct the patterns for "God Mode" (pattern: "F3 0F 11 83 A0 01 00 00") and "Infinite Stamina" (pattern: "F3 0F 11 83 30 01 00 00 F3") inside escape_the_backrooms.json?
4. Suggest how to implement the missing native memory patterns/offsets for other features in escape_the_backrooms.json (like speed_multiplier, jump_height, fly_mode, no_clip) which currently have Unity-specific Mono hook configurations but are running on an Unreal Engine game.
5. Provide the exact code or JSON corrections to make these cheats fully operational on the latest version of Escape the Backrooms.
```

---

## 🎯 Game Modding Features Reference (Wand/Went)

Below are the premium trainer features supported by tools like Went/Wand, categorized by usage:

### 1. Player Status
- **God Mode**: Prevents all damage from any source (hazards, entities).
- **Infinite Stamina**: Disables stamina depletion when sprinting, jumping, or carrying items.
- **Infinite Sanity / No Panic**: Keeps player sanity at 100%, bypassing hallucination/panic systems.
- **Infinite Battery / Oxygen**: Bypasses flashlight battery drain and swimming oxygen limit.
- **No Hunger / Thirst**: Freezes survival status meters at maximum.

### 2. Movement & Physics
- **Walk Speed Multiplier**: Adjusts the base walking velocity (e.g., 1.5x - 5x).
- **Sprint Speed Multiplier**: Boosts sprint/running speed independently.
- **Crouch Speed Multiplier**: Moves at normal speed while crouching.
- **Jump Height Multiplier**: Increases vertical jump velocity.
- **Infinite Jumps / Air Jump**: Bypasses the grounded check, allowing infinite mid-air jumps.
- **Instant Acceleration**: Removes momentum build-up, letting the player reach top speed instantly.
- **Fly Mode (Spectator)**: Disables gravity and collisions, letting the player fly vertically and horizontally.
- **No Clip**: Bypasses mesh collisions, allowing passage through walls and locked doors.

### 3. Combat & Stealth
- **Enemies Can't Kill You**: Restores health or skips death animations if damage is taken.
- **Invisibility / Ghost Mode**: Makes the player undetectable by AI entity sensors.
- **One-Hit Kill**: Sets weapon/attack damage output to maximum.
- **No Recoil / Max Accuracy**: Freezes crosshair spread and gun recoil.

### 4. World & Visuals
- **Night Vision / Full Bright**: Adjusts light levels to make dark rooms fully visible.
- **Game Speed (Time Scale)**: Speeds up or slows down the entire game simulation speed.
- **ESP (Extra Sensory Perception)**: Highlights players, items, and enemies through solid walls.

---

## 👁️ Technical ESP Implementation Guide

### 1. Unity Games (Injected Payload)
Since the `OwnWand.Payload.dll` is injected directly into the Unity Mono domain, it has access to the Unity engine API:
* **Object Discovery**: The payload can call `UnityEngine.Object.FindObjectsOfType<T>()` where `T` is `EnemyAI`, `PlayerControllerB`, or `GrabbableObject`.
* **Coordinate Projection**:
  ```csharp
  // Project 3D world position to 2D screen space
  Vector3 screenPos = Camera.main.WorldToScreenPoint(target.transform.position);
  if (screenPos.z > 0) // Target is in front of the camera
  {
      float x = screenPos.x;
      float y = Screen.height - screenPos.y; // Invert Y for GUI coordinate system
      // Draw Box/Text using Unity GUI class inside OnGUI()
  }
  ```

### 2. Unreal Engine Games (Host-side Memory Reading)
Because Unreal Engine compiles to native code without a managed runtime, drawing ESP from the host WPF app requires:
1. **Locating GWorld/UWorld Pointer**: Find the global world pointer using a signature scan in `Backrooms-Win64-Shipping.exe`.
2. **Reading Actors List**:
   * Dereference `GWorld` $\rightarrow$ `PersistentLevel` $\rightarrow$ `ActorsArray`.
   * Loop through actors to find enemy classes (comparing names via `FName` structures).
3. **Getting Coordinates**: Read the `RootComponent` coordinate vector `RelativeLocation` for each enemy.
4. **Drawing Overlay**:
   * Create a transparent, click-through WPF overlay window that stays aligned over the game window.
   * Calculate projection (WorldToScreen) in C# using the player's view matrix (retrieved from `PlayerController` $\rightarrow$ `PlayerCameraManager`).
   * Render ESP boxes/names using WPF shapes or `Direct2D` drawing.
