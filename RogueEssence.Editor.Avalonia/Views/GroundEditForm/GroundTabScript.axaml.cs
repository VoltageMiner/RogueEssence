﻿using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RogueEssence.Dev.Views
{
    public class GroundTabScript : UserControl
    {
        public GroundTabScript()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
