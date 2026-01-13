# koishi自动化

从GitHub代理 api 获取 koishijs/koishi-desktop 的最新 tag

然后 github 下载 https://gh-proxy.com/https://github.com/koishijs/koishi-desktop/releases/download/<tag>/koishi-desktop-win-x64-<tag>.zip

解压到 bin/koishi

目录结构是

bin/koishi

bin/koishi/koi.exe

## 配置

找到 data/instances/default/koishi.yml

修改

```yaml
plugins:
  group:console:
    market:<随机id>:
      search:
        endpoint: https://registry.koishi.t4wefan.pub/index.json
```

修改

```yaml
plugins:
  group:adapter:
    adapter-satori: //之前是~adapter-satori: {}
      endpoint: http://127.0.0.1:5600
```

安装 npm-check-update

```bash
bin/koishi/bin/koishi.exe bin/koishi/bin/yarn.cjs add npm-check-updates

bin/koishi/bin/koishi.exe bin/koishi/bin/yarn.cjs add npm-check-updates

cd data/instances/default
"node_modules/.bin/npm-check-updates" -u
../../../bin/koishi/bin/koishi.exe bin/yarn.cjs install
```