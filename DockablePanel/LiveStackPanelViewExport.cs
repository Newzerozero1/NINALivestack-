using System;
using System.ComponentModel.Composition;
using System.Windows;

namespace NinaLiveStack.DockablePanel {

    [Export(typeof(ResourceDictionary))]
    public class LiveStackPanelViewExport : ResourceDictionary {
        public LiveStackPanelViewExport() {
            Source = new Uri("pack://application:,,,/NinaLiveStack;component/DockablePanel/LiveStackPanelView.xaml", UriKind.Absolute);
        }
    }
}
