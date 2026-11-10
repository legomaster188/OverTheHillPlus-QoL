// Compiled into every project of the MelonLoader build only (Directory.Build.props, -p:Loader=Melon).
//
// MelonLoader's Il2Cpp assemblies put the game's types under an "Il2Cpp" prefix: the global namespace becomes Il2Cpp,
// Fusion becomes Il2CppFusion, VehiclePhysics Il2CppVehiclePhysics, and so on (UnityEngine, Il2CppSystem and
// Il2CppInterop keep their names). BepInEx's don't. So that the same source builds for both:
//   - a file's using of a prefixed namespace has both spellings: #if MELON / using Il2CppFusion; / #else / using Fusion;
//   - the game's global types are imported everywhere from Il2Cpp (under BepInEx they are in the global namespace);
//   - a name written with its namespace (Fusion.SessionInfo, FunCore.Managers.ResourcesManager) goes through the
//     aliases below.
#if MELON
// Unity's Mathf in plain C#: the interop assembly's goes through il2cpp_runtime_invoke for every call (Core/Shared/QuickMath.cs).
global using Mathf = OverTheHillPlus.Shared.QuickMath;
global using Il2Cpp;
global using AmplifyImpostors = Il2CppAmplifyImpostors;
global using AnalyticsEvents = Il2CppAnalyticsEvents;
global using Beautify = Il2CppBeautify;
global using Boxophobic = Il2CppBoxophobic;
global using Cinemachine = Il2CppCinemachine;
global using CommandTerminal = Il2CppCommandTerminal;
global using DG = Il2CppDG;
global using DigitalOpus = Il2CppDigitalOpus;
global using DirectInputManager = Il2CppDirectInputManager;
global using Discord = Il2CppDiscord;
global using DistantLands = Il2CppDistantLands;
global using EdyCommonTools = Il2CppEdyCommonTools;
global using Epic = Il2CppEpic;
global using ExitGames = Il2CppExitGames;
global using FMOD = Il2CppFMOD;
global using FMODUnity = Il2CppFMODUnity;
global using FMODUnityResonance = Il2CppFMODUnityResonance;
global using FidelityFX = Il2CppFidelityFX;
global using FunCore = Il2CppFunCore;
global using Fusion = Il2CppFusion;
global using GPUInstancerPro = Il2CppGPUInstancerPro;
global using Galaxy = Il2CppGalaxy;
global using GameAnalyticsSDK = Il2CppGameAnalyticsSDK;
global using Graphy = Il2CppGraphy;
global using HorizonBasedAmbientOcclusion = Il2CppHorizonBasedAmbientOcclusion;
global using JBooth = Il2CppJBooth;
global using JSukoAnimals = Il2CppJSukoAnimals;
global using LeTai = Il2CppLeTai;
global using MEC = Il2CppMEC;
global using MHLab = Il2CppMHLab;
global using MeshCombineStudio = Il2CppMeshCombineStudio;
global using Modio = Il2CppModio;
global using NOT_Lonely_OPT = Il2CppNOT_Lonely_OPT;
global using NanoSockets = Il2CppNanoSockets;
global using NaughtyAttributes = Il2CppNaughtyAttributes;
global using Nobi = Il2CppNobi;
global using NodeCanvas = Il2CppNodeCanvas;
global using Obi = Il2CppObi;
global using OccaSoftware = Il2CppOccaSoftware;
global using Overland = Il2CppOverland;
global using ParadoxNotion = Il2CppParadoxNotion;
global using Photon = Il2CppPhoton;
global using PixelTea = Il2CppPixelTea;
global using Polyperfect = Il2CppPolyperfect;
global using ProximityChat = Il2CppProximityChat;
global using Rowlan = Il2CppRowlan;
global using SCPE = Il2CppSCPE;
global using Sirenix = Il2CppSirenix;
global using Steamworks = Il2CppSteamworks;
global using StylizedWater3 = Il2CppStylizedWater3;
global using TMPro = Il2CppTMPro;
global using TND = Il2CppTND;
global using TSD = Il2CppTSD;
global using Tayx = Il2CppTayx;
global using TheVisualEngine = Il2CppTheVisualEngine;
global using TypeReferences = Il2CppTypeReferences;
global using Umbra = Il2CppUmbra;
global using VehiclePhysics = Il2CppVehiclePhysics;
#endif
