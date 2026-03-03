# Unity Remote Postprocess Controller 設計

## 1. 目的
- URP の `VolumeProfile` を Runtime で遠隔調整する。
- スマホブラウザから操作できる Web UI を提供する。
- Override が有効なプロパティを自動検出して UI を生成する。
- Web UI から保存した設定を、次回起動時に自動ロードする。

## 2. Package 構成
```text
Packages/cc.sugi.urp-remote-postprocess/
  package.json
  Runtime/
    Core/
    Model/
    Serialization/
    Transport/
    Web/
    Bootstrap/
  Editor/
  WebUI/
    index.html
    assets/*
  Samples~/
    BasicScene/
```

## 3. Runtime 主要クラス

## 3.1 Bootstrap
- `RemotePostprocessController` (`MonoBehaviour`)
  - 役割: 初期化オーケストレーション。
  - 責務:
    - 対象 `Volume` 登録
    - `Schema` 生成
    - HTTP/WebSocket サーバ起動
    - 起動時プリセットロード
  - 主要 API:
    - `Initialize()`
    - `ApplyPatch(StatePatch patch)`
    - `SavePreset(string presetName)`
    - `LoadPreset(string presetName)`

## 3.2 Core
- `VolumeProfileScanner`
  - 役割: `VolumeProfile` から操作可能パラメータを抽出。
  - 仕様:
    - `VolumeComponent` を列挙
    - `VolumeParameter` を reflection で列挙
    - `overrideState == true` を対象として採用

- `ParameterPathResolver`
  - 役割: パラメータを一意に識別するパスを生成。
  - パス形式:
    - `"{volumeId}/{componentType}/{parameterName}"`
    - 例: `GlobalVolume/ColorAdjustments/postExposure`

- `VolumeParameterAdapterRegistry`
  - 役割: 型ごとの read/write を共通化。
  - 対応初期型:
    - `BoolParameter`
    - `IntParameter`, `ClampedIntParameter`
    - `FloatParameter`, `ClampedFloatParameter`, `MinFloatParameter`
    - `ColorParameter`
    - `Vector2Parameter`, `Vector3Parameter`, `Vector4Parameter`

- `VolumeStateApplier`
  - 役割: 受信した差分を該当パラメータへ適用。
  - 仕様:
    - 不正パスはスキップして警告ログ
    - 値範囲は adapter 側で clamp
    - 高頻度更新はフレーム内でバッチ適用

## 3.3 Model
- `RemoteSchema`
  - `version`
  - `volumes[]`

- `RemoteVolumeSchema`
  - `volumeId`
  - `displayName`
  - `profileAssetGuid` (Editor 取得可能時)
  - `components[]`

- `RemoteComponentSchema`
  - `typeName` (例: `ColorAdjustments`)
  - `displayName`
  - `parameters[]`

- `RemoteParameterSchema`
  - `path`
  - `name`
  - `type` (`bool`, `int`, `float`, `color`, `vector2`, `vector3`, `vector4`)
  - `overrideState`
  - `min` / `max` / `step` (存在する場合)
  - `defaultValue`
  - `currentValue`

- `StatePatch`
  - `entries[]` (`path`, `value`, `overrideState?`)

- `PresetData`
  - `schemaVersion`
  - `createdAtUtc`
  - `updatedAtUtc`
  - `volumeStates[]`

## 3.4 Serialization
- `PresetRepository`
  - 役割: JSON 保存/読込。
  - 保存先:
    - `Application.persistentDataPath/RemotePostprocess/presets/{presetName}.json`
  - 主要 API:
    - `Save(PresetData data, string presetName)`
    - `Load(string presetName): PresetData`
    - `ListPresets(): IReadOnlyList<string>`

- `SchemaSerializer`
  - 役割: `RemoteSchema` JSON 生成。

## 3.5 Transport
- `HttpApiServer`
  - 役割: REST エンドポイント提供と WebUI 配信。
  - エンドポイント:
    - `GET /health`
    - `GET /schema`
    - `GET /state`
    - `PATCH /state`
    - `GET /presets`
    - `POST /presets/save`
    - `POST /presets/load`
    - `GET /` (WebUI)

- `WsEventHub`
  - 役割: リアルタイム更新配信。
  - イベント:
    - `state.updated`
    - `preset.loaded`
    - `error`

## 3.6 Web
- `WebUiAssetProvider`
  - 役割: Package 内静的ファイル配信。
  - 仕様:
    - `WebUI` 内ファイルを Content-Type 付きで返す
    - `index.html` をデフォルト返却

## 4. 依存関係
- Unity:
  - `com.unity.render-pipelines.universal`
- 推奨追加:
  - `com.cysharp.unitask` (任意: async 制御)
  - `com.unity.nuget.newtonsoft-json` (JSON 制御が必要な場合)

## 5. 通信仕様

## 5.1 `GET /schema` レスポンス例
```json
{
  "version": 1,
  "volumes": [
    {
      "volumeId": "GlobalVolume",
      "displayName": "Global Volume",
      "components": [
        {
          "typeName": "ColorAdjustments",
          "displayName": "Color Adjustments",
          "parameters": [
            {
              "path": "GlobalVolume/ColorAdjustments/postExposure",
              "name": "Post Exposure",
              "type": "float",
              "overrideState": true,
              "min": -5.0,
              "max": 5.0,
              "step": 0.01,
              "currentValue": 0.0
            }
          ]
        }
      ]
    }
  ]
}
```

## 5.2 `PATCH /state` リクエスト例
```json
{
  "entries": [
    {
      "path": "GlobalVolume/ColorAdjustments/postExposure",
      "value": 1.25
    },
    {
      "path": "GlobalVolume/ColorAdjustments/colorFilter",
      "value": { "r": 1, "g": 0.95, "b": 0.9, "a": 1 }
    }
  ]
}
```

## 5.3 `POST /presets/save` リクエスト例
```json
{
  "presetName": "Cinematic_A"
}
```

## 6. Web UI 設計（スマホ最適化）
- 画面構成:
  - ヘッダ: 接続状態、対象 Volume、プリセット選択
  - 本文: Component の Accordion
  - 下部固定: `Save`, `Load`, `Reset` ボタン
- 操作部品:
  - `float/int`: スライダー + 数値入力
  - `bool`: スイッチ
  - `color`: カラーピッカー + RGBA スライダー
  - `vector*`: 各軸入力
- UX:
  - タッチ操作優先 (44px 以上)
  - 値送信は 30ms 程度で debounce
  - 接続切断時は再接続リトライ

## 7. 起動シーケンス
1. `RemotePostprocessController.Initialize()`
2. `VolumeProfileScanner` でスキーマ生成
3. `PresetRepository.Load(defaultPreset)` を試行
4. 読込成功なら `VolumeStateApplier.Apply()`
5. HTTP/WebSocket サーバ起動
6. ローカルIPとポートをログ表示 (QR は WebUI で表示)

## 8. エラーハンドリング
- 不正 path: 400 + 失敗項目を返す
- 型不一致: 400 + expected/actual を返す
- 保存失敗: 500 + 例外要約
- 既知でない component/parameter: warning ログのみ

## 9. 最小実装スコープ (MVP)
1. `ColorAdjustments`, `Bloom`, `Vignette` のみ対応
2. `float/bool/color` のみ対応
3. 単一 Volume 固定
4. プリセット 1 つ (`default.json`) のみ

## 10. 拡張スコープ (v1.1+)
1. 全 URP 標準 Postprocess 対応
2. 複数 Volume 切替
3. プリセット複数管理 + 削除
4. 認証トークン (LAN 内誤操作防止)
5. 差分同期の最適化 (変更項目のみ WS push)

## 11. 実装タスク分解
1. Package 雛形作成 (`package.json`, asmdef, Samples~)
2. `Model` と `Schema` 生成実装
3. `AdapterRegistry` 実装
4. `StatePatch` 適用実装
5. JSON 保存/起動時ロード実装
6. HTTP API 実装
7. WebUI 実装（schema-driven）
8. サンプルシーンと導入手順作成
9. PlayMode テスト追加
10. ドキュメント整備

## 12. 受け入れ基準
- スマホから `postExposure` 変更で 100ms 以内に画面変化が確認できる。
- `Save` 後に Unity 再起動して同値が復元される。
- Override 無効項目は UI に表示されない。
- 未対応型があっても全体が停止せず、対応型は操作できる。
