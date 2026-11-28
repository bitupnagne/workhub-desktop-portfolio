using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Portfolio.OfficeSystem;

public partial class RoomAvatarView : UserControl
{
    public static readonly DependencyProperty TilesProperty =
        DependencyProperty.Register(nameof(Tiles), typeof(IEnumerable<RoomAvatarTile>), typeof(RoomAvatarView),
            new PropertyMetadata(null, (_, e) =>
            {
                if (_ is RoomAvatarView view) view.Render(e.NewValue as IEnumerable<RoomAvatarTile>);
            }));

    public IEnumerable<RoomAvatarTile>? Tiles
    {
        get => (IEnumerable<RoomAvatarTile>?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    public RoomAvatarView()
    {
        InitializeComponent();
        Loaded += (_, _) => Render(Tiles);
    }

    private void Render(IEnumerable<RoomAvatarTile>? tiles)
    {
        var list = tiles == null ? new List<RoomAvatarTile>() : new List<RoomAvatarTile>(tiles);
        var slots = new[] { Slot0, Slot1, Slot2, Slot3 };
        var images = new[] { Image0, Image1, Image2, Image3 };
        var initials = new[] { Initial0, Initial1, Initial2, Initial3 };

        for (int i = 0; i < slots.Length; i++)
        {
            slots[i].Visibility = Visibility.Collapsed;
            Grid.SetRow(slots[i], i / 2);
            Grid.SetColumn(slots[i], i % 2);
            Grid.SetRowSpan(slots[i], 1);
            Grid.SetColumnSpan(slots[i], 1);
            WebImage.SetUrl(images[i], null);
            initials[i].Text = "";
        }

        if (list.Count == 0)
        {
            list.Add(new RoomAvatarTile { Name = "?" });
        }

        for (int i = 0; i < list.Count && i < slots.Length; i++)
        {
            var tile = list[i];
            slots[i].Visibility = Visibility.Visible;
            Grid.SetRow(slots[i], tile.Row);
            Grid.SetColumn(slots[i], tile.Column);
            Grid.SetRowSpan(slots[i], tile.RowSpan);
            Grid.SetColumnSpan(slots[i], tile.ColumnSpan);
            WebImage.SetUrl(images[i], tile.HasProfileImage ? tile.ProfileImageUrl : null);
            images[i].Visibility = tile.HasProfileImage ? Visibility.Visible : Visibility.Collapsed;
            initials[i].Visibility = tile.HasProfileImage ? Visibility.Collapsed : Visibility.Visible;
            initials[i].Text = tile.Initial;
            initials[i].FontSize = list.Count == 1 ? 18 : 11;
        }
    }
}
