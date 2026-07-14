# CrabDesk 隐私说明

CrabDesk 在本机整理 Windows 桌面，不提供账户、云同步或遥测服务。

## 本地数据

- 盒子布局、分组关系、规则、主题和备份设置保存在 `%LocalAppData%\CrabDesk`。
- 布局备份只包含 CrabDesk 配置，不包含桌面文件内容。
- CrabDesk 会读取用户桌面、公共桌面和用户主动映射的文件夹，以显示文件名、图标、缩略图和文件属性。
- 文件打开、重命名、复制、删除等操作由用户主动触发，并直接作用于本机文件系统。

## 网络访问

- 检查更新只访问构建时配置的公共 GitHub Releases API 和用户主动打开的 GitHub Release 页面。
- 更新请求包含 GitHub API 所需的 `User-Agent`、版本通道和缓存用 `ETag`，不会上传 CrabDesk 配置、桌面文件列表、机器名或用户目录。
- 与任何互联网请求一样，GitHub 及网络服务提供商可能看到请求来源 IP；其处理方式受 GitHub 隐私政策约束。

## Windows 集成

CrabDesk 可按用户选择写入当前用户的开机启动项和桌面右键菜单项。退出或异常恢复时，CrabDesk 与 IconGuard 会恢复接管前的 Explorer 图标状态。

卸载应用不会自动删除 `%LocalAppData%\CrabDesk` 中的用户配置和布局备份，便于用户保留或手动清理数据。
