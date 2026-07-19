# BeeHive_VR engine — POC 3b: shared-texture → swapchain → quad.

override_functions = [
    "xrCreateSession",                # extract the app's ID3D11Device from the binding chain
    "xrDestroySession",               # tear down our resources before the runtime
    "xrEndFrame",                     # lazy-setup + append our quad layers
    "xrWaitFrame",                    # 25.6.2026: nur Timing (Stall-Watchdog) — Runtime-Backpressure vs iRacing-Render trennen
    "xrAttachSessionActionSets",      # piggyback our action set if iRacing ever attaches (defensive)
    "xrPollEvent",                    # 18.7.2026: Session-Ende FRUEH erkennen (STOPPING/EXITING) → Stall-Watchdog stummschalten
    "xrRequestExitSession",           # dito, app-initiierter Ausstieg (noch frueher)
]

# Functions our layer invokes against the runtime (beyond the bare loader set).
requested_functions = [
    "xrCreateReferenceSpace",
    "xrDestroySpace",
    "xrCreateSwapchain",
    "xrDestroySwapchain",
    "xrEnumerateSwapchainImages",
    "xrAcquireSwapchainImage",
    "xrWaitSwapchainImage",
    "xrReleaseSwapchainImage",
    # Place-in-VR — our own action set since iRacing never calls xrAttach/Sync.
    "xrStringToPath",
    "xrCreateActionSet",
    "xrDestroyActionSet",
    "xrCreateAction",
    "xrDestroyAction",
    "xrSuggestInteractionProfileBindings",
    "xrAttachSessionActionSets",
    "xrCreateActionSpace",
    "xrSyncActions",
    "xrGetActionStateBoolean",
    "xrLocateSpace",
]

extensions = []
