# BeeHive_VR engine — POC 3b: shared-texture → swapchain → quad.

override_functions = [
    "xrCreateSession",   # to extract the app's ID3D11Device from the binding chain
    "xrDestroySession",  # tear down our resources before the runtime tears down its
    "xrEndFrame",        # lazy-setup on first call; append our quad layer every call
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
]

extensions = []
