# OWA 메일 클리너 WPF 컨트롤

OWA 메일 클리너의 라이트 Fluent UI를 WPF에서 재사용하는 컨트롤 라이브러리입니다. 외부 UI 패키지 없이 `net462` 및 `net8.0-windows`를 지원합니다.

## 프로젝트 참조

애플리케이션 프로젝트에서 `OwaMailCleaner.Controls.csproj`를 참조하고, 앱 리소스에 토큰을 병합합니다.

```xml
<ResourceDictionary.MergedDictionaries>
  <ResourceDictionary Source="pack://application:,,,/OwaMailCleaner.Controls;component/Themes/Tokens.xaml" />
</ResourceDictionary.MergedDictionaries>
```

```xml
xmlns:owa="clr-namespace:OwaMailCleaner.Controls;assembly=OwaMailCleaner.Controls"
<owa:OwaButton Content="검색" />
<owa:OwaButton Content="삭제" Variant="Danger" />
<owa:OwaComboBox Label="폴더" ItemsSource="{Binding Folders}" SelectedItem="{Binding SelectedFolder}" />
<owa:OwaRadioButton GroupName="mode" Content="읽음 처리" IsChecked="True" />
<owa:OwaDataGrid ItemsSource="{Binding Messages}" />
<owa:OwaSeparator />
<owa:OwaCalendar SelectionMode="Range" RangeStart="{Binding StartDate}" RangeEnd="{Binding EndDate}" />
```

`OwaCalendar`는 `Single`과 `Range` 모드를 제공하며 `MinDate`, `MaxDate`, `DisabledDates`로 선택 가능 날짜를 제한합니다. `DisabledDates` 컬렉션을 변경한 뒤에는 `Refresh()`를 호출합니다.

## 실행 및 검증

```powershell
dotnet build .\OwaMailCleaner.Controls.sln
dotnet run --project .\OwaMailCleaner.Controls.Tests
dotnet run --project .\OwaMailCleaner.Controls.Demo
```
