﻿using System;

namespace hhnl.HomeAssistantNet.Shared.Automation
{
    [AttributeUsage(AttributeTargets.Method)]
    public class AutomationAttribute : Attribute
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="displayName">
        /// The display name of the automation.</param>
        /// <param name="runOnStart">
        /// When set to <c>true</c> the automation will be executed when the application is started.
        /// </param>
        /// <param name="reentryPolicy">
        /// The policy to use when an automation is triggered while the previous execution hasn't finished yet.
        /// See: <see cref="ReentryPolicy"/>
        /// </param>
        public AutomationAttribute(string? displayName = null, bool runOnStart = false, ReentryPolicy reentryPolicy = ReentryPolicy.Queue)
        {
        }
    }
}