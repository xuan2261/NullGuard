﻿using System;

namespace NullGuard
{
    /// <summary>
    /// Prevents injection of null checking on task result values when return value checks are enabled.
    /// </summary>
    [AttributeUsage(AttributeTargets.ReturnValue)]
    public class MaybeNullTaskResultAttribute : Attribute
    {
    }
}