using System.Windows;
using LetsSSL.App.ViewModels;

namespace LetsSSL.App.Views;

public partial class NewCertificateWindow : Window
{
    public NewCertificateWindow(NewCertificateViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnSubmit(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
