﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Options;

namespace Microsoft.VisualStudio.LanguageServices.KeybindingReset
{
    internal sealed class KeybindingResetOptions
    {
        public static readonly Option2<ReSharperStatus> ReSharperStatus = new("KeybindingResetOptions_ReSharperStatus", defaultValue: KeybindingReset.ReSharperStatus.NotInstalledOrDisabled);
        public static readonly Option2<bool> NeedsReset = new("KeybindingResetOptions_NeedsReset", defaultValue: false);
        public static readonly Option2<bool> NeverShowAgain = new("KeybindingResetOptions_NeverShowAgain", defaultValue: false);
        public static readonly Option2<bool> EnabledFeatureFlag = new("KeybindingResetOptions_EnabledFeatureFlag", defaultValue: false);
    }
}
