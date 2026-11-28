using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Portfolio.OfficeSystem;

// 새 대화 / 초대 공용 대상 선택 별도 창.
// 직원 탭: 이름 목록 선택. 조직 탭: 트리 캐스케이드 체크(상위 클릭=하위 전체 체크, 하위 해제=상위 해제).
// 두 탭은 상호 배타 — 전환 시 선택이 있으면 초기화 확인.
public partial class MemberPickerWindow : Window
{
    private readonly long _myUserId;
    private readonly List<PickEmployeeItem> _employees = new();
    private readonly List<PickOrgItem> _roots = new();        // 최상위 조직 노드들
    private readonly List<PickOrgItem> _orgItems = new();     // 평면(깊이 우선) — ItemsControl 바인딩용
    private readonly ObservableCollection<PickChipItem> _chips = new();
    private string _tab = "employees";

    public List<long> SelectedUserIds { get; private set; } = new();

    public MemberPickerWindow(string headerTitle, string confirmText,
        IEnumerable<EmployeeModel> candidates, IEnumerable<OrgNodeModel> orgTree, long myUserId)
    {
        InitializeComponent();
        _myUserId = myUserId;
        txtHeader.Text = headerTitle;
        Title = headerTitle;
        btnConfirm.Content = confirmText;

        foreach (var emp in candidates ?? Enumerable.Empty<EmployeeModel>())
        {
            if (emp.UserId == _myUserId) continue;
            _employees.Add(new PickEmployeeItem(emp));
        }
        var candidateIds = _employees.Select(p => p.UserId).ToHashSet();

        foreach (var node in orgTree ?? Enumerable.Empty<OrgNodeModel>())
        {
            var built = BuildOrgItem(node, 0, null, candidateIds);
            if (built != null)
            {
                _roots.Add(built);
                FlattenInto(_orgItems, built);
            }
        }

        empList.ItemsSource = _employees;
        orgList.ItemsSource = _orgItems;
        chipList.ItemsSource = _chips;
        UpdateTabUI();
        RebuildChips();
    }

    private PickOrgItem? BuildOrgItem(OrgNodeModel node, int depth, PickOrgItem? parent, HashSet<long> candidateIds)
    {
        var directIds = node.Members
            .Where(m => m.UserId != _myUserId && candidateIds.Contains(m.UserId))
            .Select(m => m.UserId)
            .Distinct()
            .ToList();
        var item = new PickOrgItem(node.Name ?? "", depth, directIds, parent);
        foreach (var child in node.Children)
        {
            var c = BuildOrgItem(child, depth + 1, item, candidateIds);
            if (c != null) item.Children.Add(c);
        }
        int subtree = directIds.Count + item.Children.Sum(c => c.SubtreeMemberCount);
        item.SubtreeMemberCount = subtree;
        return subtree == 0 ? null : item;
    }

    private static void FlattenInto(List<PickOrgItem> flat, PickOrgItem node)
    {
        flat.Add(node);
        foreach (var c in node.Children) FlattenInto(flat, c);
    }

    private List<long> ResultIds() => _tab == "orgs"
        ? _orgItems.Where(o => o.IsChecked).SelectMany(o => o.DirectMemberIds).Distinct().ToList()
        : _employees.Where(p => p.IsSelected).Select(p => p.UserId).ToList();

    private void RebuildChips()
    {
        _chips.Clear();
        if (_tab == "orgs")
        {
            foreach (var root in _roots) CollectChipRoots(root);
        }
        else
        {
            foreach (var p in _employees.Where(p => p.IsSelected))
                _chips.Add(new PickChipItem { Text = p.Name ?? "", EmpRef = p });
        }
        txtChipEmpty.Visibility = _chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        txtSummary.Text = $"{ResultIds().Count}명 선택";
    }

    private void CollectChipRoots(PickOrgItem node)
    {
        if (node.IsChecked)
        {
            _chips.Add(new PickChipItem { Text = node.Name, OrgRef = node });
            return; // 자식은 subsume
        }
        foreach (var c in node.Children) CollectChipRoots(c);
    }

    private void EmpRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PickEmployeeItem item)
        {
            item.IsSelected = !item.IsSelected;
            RebuildChips();
            e.Handled = true;
        }
    }

    private void OrgRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PickOrgItem item)
        {
            ToggleOrgCascade(item);
            RebuildChips();
            e.Handled = true;
        }
    }

    private void ChipRemove_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PickChipItem chip)
        {
            if (chip.EmpRef != null) chip.EmpRef.IsSelected = false;
            if (chip.OrgRef != null) ToggleOrgCascade(chip.OrgRef); // 칩 ×: 노드 + 하위 해제 + 상위 정합
            RebuildChips();
            e.Handled = true;
        }
    }

    // 토글 정책 (1.0.81 수정):
    //  · 노드 자신과 하위는 함께 토글
    //  · 상위로는 자동 전파하지 않음 — 사용자가 명시적으로 상위 체크/해제만 상위에 적용됨
    //    (예: 상위 팀 체크 → 하위 그룹 모두 체크. 하위 그룹 하나만 해제 → 상위 팀은 체크 유지)
    //  · IsPartial 은 전체 트리에 대해 재계산해서 시각 표시(─)만 갱신
    private void ToggleOrgCascade(PickOrgItem node)
    {
        bool willCheck = !node.IsChecked;
        ApplyCheckRecursive(node, willCheck);
        foreach (var root in _roots) UpdatePartialRecursive(root);
    }

    private static void ApplyCheckRecursive(PickOrgItem node, bool check)
    {
        node.IsChecked = check;
        foreach (var c in node.Children) ApplyCheckRecursive(c, check);
    }

    // 반환: 이 노드 또는 자손 중 체크된 게 있는지 (자기는 IsPartial 갱신)
    private static bool UpdatePartialRecursive(PickOrgItem node)
    {
        bool descendantHasChecked = false;
        foreach (var c in node.Children)
        {
            if (UpdatePartialRecursive(c)) descendantHasChecked = true;
        }
        node.IsPartial = !node.IsChecked && descendantHasChecked;
        return node.IsChecked || descendantHasChecked;
    }

    private void TabEmployees_Click(object sender, RoutedEventArgs e) => SwitchTab("employees");
    private void TabOrgs_Click(object sender, RoutedEventArgs e) => SwitchTab("orgs");

    private void SwitchTab(string tab)
    {
        if (_tab == tab) return;
        bool hasSel = _tab == "orgs" ? _orgItems.Any(o => o.IsChecked) : _employees.Any(p => p.IsSelected);
        if (hasSel)
        {
            var confirm = new UpdatePromptWindow("탭 변경", "탭을 바꾸면 선택된 대상이 모두 해제됩니다. 계속하시겠습니까?", true, 240, "변경", "취소") { Owner = this };
            if (confirm.ShowDialog() != true) return;
        }
        foreach (var p in _employees) p.IsSelected = false;
        foreach (var o in _orgItems) { o.IsChecked = false; o.IsPartial = false; }
        _tab = tab;
        UpdateTabUI();
        RebuildChips();
    }

    private void UpdateTabUI()
    {
        bool emp = _tab == "employees";
        panelEmployees.Visibility = emp ? Visibility.Visible : Visibility.Collapsed;
        panelOrgs.Visibility = emp ? Visibility.Collapsed : Visibility.Visible;
        var brandLight = (Brush)FindResource("BrandLightBrush");
        tabEmpBg.Background = emp ? brandLight : Brushes.Transparent;
        tabOrgBg.Background = emp ? Brushes.Transparent : brandLight;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = txtSearch.Text.Trim().ToLower();
        empList.ItemsSource = string.IsNullOrWhiteSpace(q)
            ? _employees
            : _employees.Where(p => (p.Name?.ToLower().Contains(q) ?? false) || (p.TeamName?.ToLower().Contains(q) ?? false)).ToList();
    }

    // [1.0.83] 모달리스 모드 — Show() 로 열면 ShowDialog 처럼 다른 창(메신저 등) 입력을 막지 않음.
    // 결과는 Confirmed 이벤트로 전달. ShowDialog 호환을 위해 DialogResult 도 그대로 설정.
    public event Action<List<long>>? Confirmed;
    public event Action? Cancelled;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var ids = ResultIds();
        if (ids.Count == 0)
        {
            new UpdatePromptWindow("알림", "한 명 이상 선택하세요.", false, 220) { Owner = this }.ShowDialog();
            return;
        }
        SelectedUserIds = ids;
        // ShowDialog 호환 (모달 모드) — Show() 모드일 땐 InvalidOperationException 무시
        try { DialogResult = true; } catch { }
        Confirmed?.Invoke(ids);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { }
        Cancelled?.Invoke();
        Close();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}

public class PickEmployeeItem : INotifyPropertyChanged
{
    private readonly EmployeeModel _emp;
    public PickEmployeeItem(EmployeeModel emp) { _emp = emp; }
    public long UserId => _emp.UserId;
    public string? Name => _emp.Name;
    public string? TeamName => _emp.TeamName;
    private bool _sel;
    public bool IsSelected { get => _sel; set { _sel = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class PickOrgItem : INotifyPropertyChanged
{
    public PickOrgItem(string name, int depth, List<long> directMemberIds, PickOrgItem? parent)
    {
        Name = name; Depth = depth; DirectMemberIds = directMemberIds; Parent = parent;
    }
    public string Name { get; }
    public int Depth { get; }
    public List<long> DirectMemberIds { get; }
    public PickOrgItem? Parent { get; }
    public List<PickOrgItem> Children { get; } = new();
    public int SubtreeMemberCount { get; set; }
    public string CountText => $"{SubtreeMemberCount}명";
    public Thickness Indent => new Thickness(10 + Depth * 16, 0, 0, 0);
    private bool _checked;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSelected)); }
    }
    // 기존 XAML 스타일(PickRow/CheckDot 의 IsSelected 바인딩) 호환용 alias.
    public bool IsSelected => IsChecked;
    private bool _partial;
    public bool IsPartial
    {
        get => _partial;
        set { if (_partial == value) return; _partial = value; OnPropertyChanged(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class PickChipItem
{
    public string Text { get; set; } = "";
    public string CountText { get; set; } = "";
    public PickEmployeeItem? EmpRef { get; set; }
    public PickOrgItem? OrgRef { get; set; }
}
