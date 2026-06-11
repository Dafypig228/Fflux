using System;
using System.Threading.Tasks;
using System.Windows;

namespace FluxCore
{
    public partial class MainWindow
    {
        // =========================================
        // PERMISSION DIALOG SYSTEM
        //
        // NOTE: This file used to contain a second, legacy command-execution
        // pipeline (ExecuteWithPermissionAsync + its own ExtractAllCommands with
        // WRITE_FILE/RUN_NODE/DOWNLOAD_FILE commands that existed nowhere else).
        // It was unreachable — nothing called it — and shadowed the real pipeline
        // in JarvisCore. Removed; original preserved in .cleanup-backup\.
        // =========================================

        // Screen Access Permission (for Smart Mode)
        private TaskCompletionSource<bool>? _screenAccessResult;

        private void Btn_PermissionAllow_Click(object sender, RoutedEventArgs e)
        {
            PermissionOverlay.Visibility = Visibility.Collapsed;
            _permissionResult?.TrySetResult(true);
            _screenAccessResult?.TrySetResult(true);
        }

        private void Btn_PermissionDeny_Click(object sender, RoutedEventArgs e)
        {
            PermissionOverlay.Visibility = Visibility.Collapsed;
            _permissionResult?.TrySetResult(false);
            _screenAccessResult?.TrySetResult(false);
        }

        /// <summary>
        /// Shows screen access permission dialog for Smart Mode.
        /// Called when AI needs to use screen-based commands.
        /// </summary>
        private async Task<bool> RequestScreenAccessAsync(string reason)
        {
            _screenAccessResult = new TaskCompletionSource<bool>();

            await Dispatcher.InvokeAsync(() =>
            {
                PermissionActionText.Text = "🖥️ Screen Access Required:";
                PermissionDetailsText.Text = $"{reason}\n\nAllow Flux to view and interact with your screen?";
                PermissionOverlay.Visibility = Visibility.Visible;

                // Make window visible if hidden
                if (this.Opacity < 0.5)
                    this.Opacity = 1;
            });

            return await _screenAccessResult.Task;
        }

        /// <summary>
        /// Generic confirmation dialog — reused by FluxBrain (intent gate) and JarvisCore (destructive gate).
        /// Reuses the existing PermissionOverlay UI.
        /// </summary>
        private async Task<bool> RequestConfirmationAsync(string question)
        {
            _permissionResult = new TaskCompletionSource<bool>();
            await Dispatcher.InvokeAsync(() =>
            {
                PermissionActionText.Text = "🤔 Confirm:";
                PermissionDetailsText.Text = question;
                PermissionOverlay.Visibility = Visibility.Visible;
                if (this.Opacity < 0.5) this.Opacity = 1;
            });
            return await _permissionResult.Task;
        }
    }
}
