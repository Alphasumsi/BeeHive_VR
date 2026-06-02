# BeeHive_VR engine — POC 3b: shared-texture → swapchain → quad.

override_functions = [
    "xrCreateSession",                # extract the app's ID3D11Device from the binding chain
    "xrDestroySession",               # tear down our resources before the runtime
    "xrEndFrame",                     # lazy-setup + append our quad layers
    "xrAttachSessionActionSets",      # piggyback our action set if iRacing ever attaches (defensive)
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
