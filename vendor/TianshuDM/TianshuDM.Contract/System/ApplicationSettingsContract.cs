namespace TianshuDM.Contract.System;

public sealed record ApplicationSettingsResponse(bool AllowLubanFailureConfirmation);

public sealed record UpdateApplicationSettingsRequest(bool AllowLubanFailureConfirmation);
