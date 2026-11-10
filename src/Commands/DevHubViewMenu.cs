namespace StartScreen.Commands
{
    internal sealed class DevHubViewMenu
    {
        internal static DevHubViewMenu Current { get; set; }
        internal Func<bool> GetShowSnoozedItems { get; set; }
        internal Func<Task> ToggleSnoozedItemsAsync { get; set; }

        internal static void Update(OleMenuCommand command)
        {
            var target = Current;
            command.Visible = target != null;
            command.Enabled = command.Visible;
            command.Checked = target?.GetShowSnoozedItems() == true;
        }

        internal static Task ExecuteAsync()
        {
            var target = Current ?? throw new InvalidOperationException("The activity options menu is no longer available.");
            return target.ToggleSnoozedItemsAsync();
        }
    }
}
