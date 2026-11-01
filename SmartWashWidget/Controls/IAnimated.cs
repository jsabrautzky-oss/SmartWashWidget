using System;

namespace SmartWashWidget.Controls;

/// <summary>Wird pro Frame vom AnimationEngine mit der echten Framezeit aufgerufen.</summary>
public interface IAnimated
{
    void Animate(double deltaSeconds, double totalTime);
}
