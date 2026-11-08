import XCTest

/// Runs against an installed, isolated copy of the real .NET + SwiftUI app.
/// The copy has books/reader data but no credentials or sync configuration.
final class NativeFrontendTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        print("KKINDLE_NATIVE_UI_REVIEW_20261107_V2")
        let bundle = name.contains("testLegacyUpgrade") ? "com.kingstacker.kkindle.nativeupgradepreview" : "com.kingstacker.kkindle.nativepreview"
        app = XCUIApplication(bundleIdentifier: bundle)
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 30))
    }

    override func tearDownWithError() throws {
        if let app {
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testNativeTabsAndNavigation() {
        for (name, identifier) in [("阅读资料", "page.materials"), ("阅读数据", "page.dashboard"), ("我的", "page.profile")] {
            app.tabBars.buttons[name].tap()
            XCTAssertTrue(app.staticTexts[identifier].waitForExistence(timeout: 15))
            attachScreenshot(name)
        }
        app.tabBars.buttons["书库"].tap()
        app.buttons["library.books"].tap()
        XCTAssertTrue(app.buttons["library.books"].exists)
        attachScreenshot("书库")
        app.buttons["library.search.toggle"].tap()
        XCTAssertTrue(app.textFields["library.search"].waitForExistence(timeout: 5))
        app.buttons["library.search.toggle"].tap()
        XCTAssertFalse(app.textFields["library.search"].exists)
    }

    /// Run on a fresh isolated preview seed, which has no sync key or credentials.
    func testBackupEncryptionKeyPrompt() {
        app.tabBars.buttons["我的"].tap()
        for identifier in ["profile.backup.export", "profile.backup.restore"] {
            let button = app.buttons[identifier]
            reveal(button); button.tap()
            let prompt = app.alerts["操作失败"]
            XCTAssertTrue(prompt.waitForExistence(timeout: 15))
            let message = prompt.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "设置同步密钥")).firstMatch
            XCTAssertTrue(message.exists)
            XCTAssertTrue(message.label.contains("密钥不会上传"))
            XCTAssertTrue(message.label.contains("遗忘后无法解密"))
            attachScreenshot("备份-先设置同步密钥")
            prompt.buttons["好"].tap()
            XCTAssertTrue(waitUntil { !prompt.exists })
        }
    }

    func testReadingCalendarPeriodPicker() {
        app.tabBars.buttons["阅读数据"].tap()
        let picker = app.segmentedControls["dashboard.period"]
        XCTAssertTrue(picker.waitForExistence(timeout: 20))
        XCTAssertFalse(picker.buttons["14 天"].exists)
        for (label, title) in [("周", "本周阅读时长"), ("月", "本月阅读时长"), ("年", "今年阅读时长")] {
            picker.buttons[label].tap()
            XCTAssertTrue(app.staticTexts[title].waitForExistence(timeout: 10))
        }
        attachScreenshot("阅读数据-周月年")
    }

    /// Requires the two local account servers at 5088 and 5089, each with an isolated test database.
    func testRegionalAccountRegistrationAndCloudSync() {
        let username = "region_" + String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12))
        let password = "fictional-native-password"
        print("KKINDLE_FICTIONAL_ACCOUNT:\(username)")
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        let regions = app.segmentedControls["account.region"]
        XCTAssertTrue(regions.waitForExistence(timeout: 15))
        XCTAssertEqual(regions.buttons.count, 2)
        if app.buttons["account.logout"].exists { tapAccountElement(app.buttons["account.logout"]) }
        tapAccountElement(regions.buttons["中国区"])
        XCTAssertEqual(app.textFields["account.server"].value as? String, "http://127.0.0.1:5088")
        register()
        verifySignOutAndLogin()
        attachScreenshot("账号-中国区")
        tapAccountElement(app.navigationBars["用户账号"].buttons.firstMatch)
        syncCurrentAccount()
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(regions.waitForExistence(timeout: 10))
        tapAccountElement(regions.buttons["国际区"])
        XCTAssertTrue(app.staticTexts["account.restart"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.textFields["account.server"].value as? String, "http://127.0.0.1:5089")
        register()
        verifySignOutAndLogin()
        attachScreenshot("账号-国际区-重开提示")
        app.terminate(); app.launch()
        XCTAssertTrue(app.tabBars.buttons["我的"].waitForExistence(timeout: 30))
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch.exists)
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 15))
        XCTAssertEqual(app.staticTexts["account.username"].label, username)
        XCTAssertFalse(app.staticTexts["account.restart"].exists)
        tapAccountElement(app.navigationBars["用户账号"].buttons.firstMatch)
        syncCurrentAccount()
        app.tabBars.buttons["书库"].tap()
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch.exists)
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(regions.waitForExistence(timeout: 10))
        tapAccountElement(regions.buttons["中国区"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 15))
        XCTAssertEqual(app.staticTexts["account.username"].label, username)
        app.terminate(); app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 30))
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch.waitForExistence(timeout: 15))

        func register() {
            let mode = app.segmentedControls["account.mode"]
            XCTAssertTrue(mode.waitForExistence(timeout: 10))
            tapAccountElement(mode.buttons["注册"])
            let user = app.textFields["account.usernameField"]
            reveal(user); user.tap(); user.typeText(username); dismissKeyboard()
            let secret = app.secureTextFields["account.password"]
            typePassword(secret, password)
            XCTAssertEqual((secret.value as? String)?.count, password.count, "The password field must retain every typed character.")
            let confirmation = app.secureTextFields["account.confirmation"]
            typePassword(confirmation, password)
            XCTAssertEqual((confirmation.value as? String)?.count, password.count)
            tapAccountElement(app.buttons["account.submit"])
            XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
            XCTAssertEqual(app.staticTexts["account.username"].label, username)
        }

        func verifySignOutAndLogin() {
            tapAccountElement(app.buttons["account.logout"])
            let mode = app.segmentedControls["account.mode"]
            XCTAssertTrue(mode.waitForExistence(timeout: 15))
            tapAccountElement(mode.buttons["登录"])
            let secret = app.secureTextFields["account.password"]
            typePassword(secret, "fictional-wrong-password")
            tapAccountElement(app.buttons["account.submit"])
            XCTAssertTrue(app.staticTexts["用户名或密码错误。"].waitForExistence(timeout: 15))
            XCTAssertFalse(app.staticTexts["account.username"].exists)
            let cleared = secret.value as? String ?? ""
            XCTAssertTrue(cleared.isEmpty || cleared == secret.placeholderValue, "Failed login must clear the password.")
            typePassword(secret, password)
            tapAccountElement(app.buttons["account.submit"])
            XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
            XCTAssertEqual(app.staticTexts["account.username"].label, username)
        }

        func typePassword(_ field: XCUIElement, _ value: String) {
            reveal(field); field.tap()
            // iOS can suggest a strong password after the first character.
            // Dismiss that system sheet before typing the rest of this fictional password.
            field.typeText(String(value.prefix(1)))
            let suggestion = app.staticTexts["使用强密码？"]
            if suggestion.waitForExistence(timeout: 2) {
                let close = app.buttons.matching(NSPredicate(format: "label IN {'关闭', 'Close', '取消', 'Cancel'}")).firstMatch
                XCTAssertTrue(close.exists, "The strong-password sheet must provide a dismiss button. Buttons: \(app.buttons.allElementsBoundByIndex.map(\.label))")
                close.tap()
                XCTAssertTrue(waitUntil { !suggestion.exists })
                // The system can reset the initial character when returning
                // focus to the secure field after declining its suggestion.
                field.typeText(XCUIKeyboardKey.delete.rawValue)
                field.typeText(value)
            } else {
                field.typeText(String(value.dropFirst()))
            }
            dismissKeyboard()
        }

        func syncCurrentAccount() {
            tapAccountElement(app.buttons["profile.sync"])
            let providers = app.segmentedControls["sync.provider"]
            XCTAssertTrue(providers.waitForExistence(timeout: 15))
            tapAccountElement(providers.buttons["账号云同步"])
            let encryption = app.secureTextFields["同步密钥（账号云同步与备份必填）"]
            XCTAssertTrue(encryption.waitForExistence(timeout: 10))
            let encryptionValue = encryption.value as? String ?? ""
            if encryptionValue.isEmpty || encryptionValue == encryption.placeholderValue {
                typePassword(encryption, "fictional-ios-sync-test-key")
            }
            let run = app.buttons["sync.run"]
            reveal(run); tapAccountElement(run)
            XCTAssertTrue(waitUntil { self.app.staticTexts["sync.status"].label.hasPrefix("同步完成") })
            attachScreenshot("账号云同步")
            tapAccountElement(app.navigationBars["同步"].buttons.firstMatch)
        }
    }

    /// Local demo only: registration, member controls, pending order cancellation and current-device revocation.
    func testAccountMembershipDevicesAndOrders() {
        let username = "region_" + String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12))
        let password = "fictional-native-password"
        print("KKINDLE_FICTIONAL_ACCOUNT:\(username)")
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        let regions = app.segmentedControls["account.region"]
        XCTAssertTrue(regions.waitForExistence(timeout: 15))
        if app.buttons["account.logout"].exists { tapAccountElement(app.buttons["account.logout"]) }
        tapAccountElement(regions.buttons["中国区"])
        let mode = app.segmentedControls["account.mode"]
        XCTAssertTrue(mode.waitForExistence(timeout: 15))
        tapAccountElement(mode.buttons["注册"])
        let user = app.textFields["account.usernameField"]
        reveal(user); replaceText(user, with: username); dismissKeyboard()
        enterPassword(app.secureTextFields["account.password"])
        enterPassword(app.secureTextFields["account.confirmation"])
        tapAccountElement(app.buttons["account.submit"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
        XCTAssertEqual(app.staticTexts["account.username"].label, username)
        let tier = app.staticTexts["membership.tier"]
        reveal(tier)
        XCTAssertTrue(tier.waitForExistence(timeout: 20))
        XCTAssertEqual(tier.label, "Free 会员")
        let current = app.staticTexts.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.device.' AND label CONTAINS '当前设备'")).firstMatch
        reveal(current)
        XCTAssertTrue(current.waitForExistence(timeout: 15))
        attachScreenshot("会员-Free与当前设备")
        let buy = app.buttons["membership.buy.pro-30d"]
        reveal(buy); tapAccountElement(buy)
        let order = app.staticTexts.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.order.'")).firstMatch
        reveal(order)
        XCTAssertTrue(order.waitForExistence(timeout: 20))
        XCTAssertTrue(order.label.contains("待确认付款"))
        let orderIdentifier = order.identifier
        let cancel = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.cancel.'")).firstMatch
        reveal(cancel); tapAccountElement(cancel)
        // Removing the cancellation controls changes the List row height;
        // SwiftUI can recycle its accessibility element outside the viewport.
        let cancelled = app.staticTexts[orderIdentifier]
        reveal(cancelled)
        XCTAssertTrue(waitUntil { cancelled.exists && cancelled.label.contains("已取消") }, app.debugDescription)
        attachScreenshot("会员-订单取消")
        let kick = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.kick.'")).firstMatch
        for _ in 0..<8 { if kick.exists && kick.isHittable { break }; app.swipeDown() }
        tapAccountElement(kick)
        let confirm = app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "踢出 ")).firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 10)); confirm.tap()
        let invalidation = app.staticTexts["设备已失效"]
        XCTAssertTrue(invalidation.waitForExistence(timeout: 15))
        attachScreenshot("会员-设备失效提示")
        tapAccountElement(app.buttons["知道了"])
        XCTAssertTrue(mode.waitForExistence(timeout: 15))
        XCTAssertFalse(app.staticTexts["account.username"].exists)

        func enterPassword(_ field: XCUIElement) {
            reveal(field); field.tap(); field.typeText(String(password.prefix(1)))
            let suggestion = app.staticTexts["使用强密码？"]
            if suggestion.waitForExistence(timeout: 2) {
                let close = app.buttons.matching(NSPredicate(format: "label IN {'关闭', 'Close', '取消', 'Cancel'}")).firstMatch
                XCTAssertTrue(close.exists); close.tap(); XCTAssertTrue(waitUntil { !suggestion.exists })
                field.typeText(XCUIKeyboardKey.delete.rawValue); field.typeText(password)
            } else { field.typeText(String(password.dropFirst())) }
            dismissKeyboard()
        }
    }

    func testSyncKeyboardCanDismissAndRefocusAndSaveDisabledDraft() {
        app.tabBars.buttons["我的"].tap()
        app.buttons["profile.sync"].tap()
        XCTAssertTrue(app.navigationBars["同步"].waitForExistence(timeout: 15))
        app.segmentedControls.buttons["WebDAV"].tap()
        let endpoint = app.textFields["服务地址"]
        XCTAssertTrue(endpoint.waitForExistence(timeout: 10))
        replaceText(endpoint, with: "https://example.invalid/kkindle")
        dismissKeyboard()
        let username = app.textFields["用户名"]
        username.tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5))
        replaceText(username, with: "native-keyboard-test")
        dismissKeyboard()
        app.buttons["sync.save"].tap()
        XCTAssertTrue(app.staticTexts["已保存"].waitForExistence(timeout: 10))
        attachScreenshot("同步设置")
        app.navigationBars["同步"].buttons.firstMatch.tap()
        app.buttons["profile.sync"].tap()
        XCTAssertEqual(app.textFields["用户名"].value as? String, "native-keyboard-test")
    }

    func testCollectionPromptCanBeOpenedAgainAfterKeyboardDismissal() {
        app.buttons["library.collections"].tap()
        for _ in 0..<2 {
            app.buttons["library.import"].tap()
            let input = app.textFields["name.input"]
            XCTAssertTrue(input.waitForExistence(timeout: 10))
            input.tap()
            input.typeText("原生测试-" + String(UUID().uuidString.prefix(6)))
            app.buttons["name.save"].tap()
            XCTAssertTrue(waitUntil { !input.exists })
        }
        attachScreenshot("收藏夹")
        app.buttons["library.books"].tap()
    }

    func testOpenedCollectionSharesLibraryGridAndListAndCanRemoveBooks() {
        app.buttons["library.books"].tap()
        if app.buttons["library.layout"].label == "网格视图" { app.buttons["library.layout"].tap() }
        let source = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(source.waitForExistence(timeout: 15))
        let bookIdentifier = source.identifier
        let bookId = String(bookIdentifier.dropFirst("book.".count))
        let gridFrame = source.frame
        attachScreenshot("书库封面网格")

        app.buttons["library.collections"].tap()
        app.buttons["library.import"].tap()
        let name = "同款书架-" + String(UUID().uuidString.prefix(6))
        let input = app.textFields["name.input"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        input.tap(); input.typeText(name)
        app.buttons["name.save"].tap()
        XCTAssertTrue(waitUntil { !input.exists })
        let folder = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.' AND label CONTAINS %@", name)).firstMatch
        XCTAssertTrue(folder.waitForExistence(timeout: 10))
        reveal(folder); folder.tap()
        app.buttons["collection.add"].tap()
        let picker = app.buttons["collection.pick.\(bookId)"]
        XCTAssertTrue(picker.waitForExistence(timeout: 10))
        picker.tap()
        XCTAssertTrue(waitUntil { !picker.exists })
        // Include the PDF and another local book to check a complete grid row.
        for _ in 0..<2 {
            let next = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.pick.'")).firstMatch
            guard next.exists else { break }
            let identifier = next.identifier
            next.tap()
            XCTAssertTrue(waitUntil { !self.app.buttons[identifier].exists })
        }
        app.buttons["完成"].tap()
        let collectedBook = app.buttons[bookIdentifier]
        XCTAssertTrue(collectedBook.waitForExistence(timeout: 10))
        XCTAssertEqual(collectedBook.frame.width, gridFrame.width, accuracy: 1)
        XCTAssertEqual(collectedBook.frame.height, gridFrame.height, accuracy: 2)
        let gridBooks = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).allElementsBoundByIndex
        for book in gridBooks {
            XCTAssertEqual(book.frame.minY, collectedBook.frame.minY, accuracy: 1)
        }
        attachScreenshot("收藏夹同款封面网格")

        app.buttons["collection.layout"].tap()
        XCTAssertTrue(waitUntil { collectedBook.frame.width > gridFrame.width * 2 })
        let listFrame = collectedBook.frame
        attachScreenshot("收藏夹同款图书列表")
        collectedBook.tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.back"].tap()
        XCTAssertTrue(app.buttons["collection.back"].waitForExistence(timeout: 10))
        app.buttons["collection.back"].tap()
        app.buttons["library.books"].tap()
        XCTAssertEqual(app.buttons["library.layout"].label, "网格视图")
        XCTAssertTrue(source.waitForExistence(timeout: 10))
        XCTAssertEqual(source.frame.width, listFrame.width, accuracy: 1)
        XCTAssertEqual(source.frame.height, listFrame.height, accuracy: 1)
        attachScreenshot("书库同款图书列表")

        app.buttons["library.collections"].tap()
        reveal(folder); folder.tap()
        XCTAssertTrue(collectedBook.waitForExistence(timeout: 10))
        collectedBook.press(forDuration: 1)
        let remove = app.buttons["移出收藏夹"]
        XCTAssertTrue(remove.waitForExistence(timeout: 10))
        remove.tap()
        XCTAssertTrue(waitUntil { !collectedBook.exists })
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch.exists)
    }

    func testReaderSearchExpandsInToolbarAndJumpsToText() throws {
        try openBook(containing: "Native UI EPUB")
        let search = app.buttons["reader.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 40))
        let position = app.staticTexts["reader.position"]
        let initialPosition = position.label
        app.sliders["reader.progress"].adjust(toNormalizedSliderPosition: 0.85)
        XCTAssertTrue(waitUntil { position.label != initialPosition })
        let beforeSearch = position.label
        search.tap()
        let input = app.textFields["reader.search.query"]
        XCTAssertTrue(input.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertEqual(app.textFields.count, 1, "Reuse one input in the current reader toolbar.")
        XCTAssertLessThan(input.frame.maxX, search.frame.minX)
        XCTAssertEqual(input.frame.midY, search.frame.midY, accuracy: 2)
        XCTAssertLessThan(input.frame.maxY, app.frame.height * 0.2)
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5))
        attachScreenshot("阅读搜索框向左展开")
        input.typeText("kkindle-no-match-" + String(UUID().uuidString.prefix(8)))
        search.tap()
        XCTAssertTrue(app.staticTexts["没有找到相关文字"].waitForExistence(timeout: 30))
        XCTAssertFalse(app.keyboards.firstMatch.exists)
        XCTAssertFalse(app.buttons["reader.排版"].isHittable)
        replaceText(input, with: "田鼠")
        search.tap()
        let result = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.'")).firstMatch
        XCTAssertTrue(result.waitForExistence(timeout: 30))
        XCTAssertTrue(result.isHittable)
        XCTAssertGreaterThan(result.frame.minY, input.frame.maxY)
        XCTAssertLessThan(result.frame.maxY, app.frame.height * 0.55)
        XCTAssertEqual(app.textFields.count, 1)
        attachScreenshot("阅读搜索结果浮层")
        result.tap()
        XCTAssertTrue(waitUntil { !input.exists && !self.app.keyboards.firstMatch.exists })
        XCTAssertTrue(position.waitForExistence(timeout: 10))
        XCTAssertNotEqual(position.label, beforeSearch)
        attachScreenshot("搜索结果跳转正文")

        let jumpedPosition = position.label
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        if !search.exists { center.tap() }
        XCTAssertTrue(search.waitForExistence(timeout: 10))
        search.tap()
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        XCTAssertEqual(input.value as? String, "田鼠")
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5))
        app.buttons["reader.search.close"].tap()
        XCTAssertTrue(waitUntil { !input.exists && !self.app.keyboards.firstMatch.exists })
        XCTAssertEqual(position.label, jumpedPosition)
        XCTAssertTrue(app.buttons["reader.close"].isHittable)
    }

    func testReaderParagraphSpacingThreeLevelsPersist() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        typography.tap()
        let spacing = app.segmentedControls["typography.paragraphSpacing"]
        reveal(spacing)
        XCTAssertTrue(spacing.isHittable)
        for title in ["紧凑", "标准", "宽松", "标准"] {
            let choice = spacing.buttons[title]
            choice.tap()
            XCTAssertTrue(choice.isSelected)
        }
        assertCompactTypographyPanel()
        attachScreenshot("阅读设置段间距三级调整")
        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.read"].tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        typography.tap()
        reveal(spacing)
        XCTAssertTrue(spacing.buttons["标准"].isSelected)
    }

    func testReaderSettingsScrollAndBlockUnderlyingButtons() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        attachScreenshot("阅读工具栏")
        typography.tap()
        XCTAssertTrue(app.segmentedControls["typography.paper"].waitForExistence(timeout: 10))
        assertCompactTypographyPanel()
        XCTAssertFalse(app.buttons["reader.bookmark"].isHittable)
        let paper = app.segmentedControls["typography.paper"]
        let newPaper = paper.buttons["黑"].isSelected ? "米" : "黑"
        paper.buttons[newPaper].tap()
        XCTAssertTrue(paper.buttons[newPaper].isSelected)
        let fontScale = app.staticTexts["typography.fontScale"]
        let initialScale = try XCTUnwrap(Int(fontScale.label.replacingOccurrences(of: "%", with: "")))
        let delta = initialScale >= 180 ? -10 : 10
        let expectedScale = min(180, max(80, initialScale + delta))
        let fontButton = app.buttons[delta > 0 ? "增大字号" : "减小字号"]
        fontButton.tap()
        XCTAssertTrue(waitUntil { fontScale.label == "\(expectedScale)%" },
                      "Font did not change: before=\(initialScale)% expected=\(expectedScale)% actual=\(fontScale.label); \(fontButton.debugDescription)")
        attachScreenshot("阅读设置")

        let indent = app.switches["typography.indent"]
        reveal(indent)
        XCTAssertTrue(indent.isHittable)
        let newIndent = indent.value as? String == "1" ? "0" : "1"
        indent.tap()
        XCTAssertTrue(waitUntil { indent.value as? String == newIndent }, "Switch did not change: \(indent.debugDescription)")
        let vertical = app.segmentedControls["typography.direction"].buttons["竖排"]
        reveal(vertical)
        let newDirection = vertical.isSelected ? "横排" : "竖排"
        let direction = app.segmentedControls["typography.direction"].buttons[newDirection]
        direction.tap()
        XCTAssertTrue(direction.isSelected)
        attachScreenshot("阅读设置滚动")
        app.buttons["typography.done"].tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.read"].tap()
        XCTAssertTrue(app.buttons["reader.排版"].waitForExistence(timeout: 40))
        app.buttons["reader.排版"].tap()
        XCTAssertTrue(app.segmentedControls["typography.paper"].buttons[newPaper].isSelected)
        XCTAssertEqual(app.staticTexts["typography.fontScale"].label, "\(expectedScale)%")
        reveal(app.switches["typography.indent"])
        XCTAssertTrue(waitUntil { self.app.switches["typography.indent"].value as? String == newIndent })
        let savedDirection = app.segmentedControls["typography.direction"].buttons[newDirection]
        reveal(savedDirection)
        XCTAssertTrue(savedDirection.isSelected)
    }

    func testPdfUsesNativeDisplaySettings() throws {
        try openBook(containing: "Native UI PDF")
        XCTAssertTrue(app.buttons["reader.显示"].waitForExistence(timeout: 40))
        app.buttons["reader.显示"].tap()
        let zoom = app.segmentedControls.buttons["1.5 倍"]
        XCTAssertTrue(zoom.waitForExistence(timeout: 10))
        XCTAssertFalse(app.segmentedControls["typography.paragraphSpacing"].exists)
        assertCompactTypographyPanel()
        zoom.tap()
        XCTAssertTrue(zoom.isSelected)
        XCTAssertFalse(app.buttons["reader.bookmark"].isHittable)
        attachScreenshot("PDF 显示设置")
    }

    /// The host installs/launches the legacy frontend, then updates its binary
    /// with --preserve-data. This checks the real restored UIKit scene session.
    func testLegacyUpgradePreservesLibraryAndPreferences() throws {
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(app.segmentedControls["profile.theme"].buttons["浅色"].isSelected)
        app.tabBars.buttons["书库"].tap()
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        typography.tap()
        XCTAssertTrue(app.segmentedControls["typography.paper"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.segmentedControls["typography.paper"].buttons["米"].isSelected)
        XCTAssertEqual(app.staticTexts["typography.fontScale"].label, "125%")
        reveal(app.switches["typography.indent"])
        XCTAssertEqual(app.switches["typography.indent"].value as? String, "1")
        attachScreenshot("覆盖安装后保留阅读设置")
    }

    func testReaderSurfaceTapsAndSwipesPassThroughTheNativeChrome() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        center.tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        let position = app.staticTexts["reader.position"]
        let before = position.label
        let start = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.45))
        let end = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.45))
        start.press(forDuration: 0.05, thenDragTo: end)
        XCTAssertTrue(waitUntil { position.label != before })
        center.tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
    }

    func testNativeConfirmationCanCancelAndReopen() {
        app.buttons["library.collections"].tap()
        app.buttons["library.import"].tap()
        let name = "确认测试-" + String(UUID().uuidString.prefix(6))
        let input = app.textFields["name.input"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        input.tap(); input.typeText(name)
        app.buttons["name.save"].tap()
        XCTAssertTrue(waitUntil { !input.exists })
        let folder = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.' AND label CONTAINS %@", name)).firstMatch
        XCTAssertTrue(folder.waitForExistence(timeout: 10))
        reveal(folder); folder.tap()
        for _ in 0..<2 {
            let menu = app.buttons["收藏夹选项"]
            XCTAssertTrue(waitUntil { menu.isEnabled })
            menu.tap(); app.buttons["清空书籍"].tap()
            XCTAssertTrue(app.alerts["清空收藏夹"].waitForExistence(timeout: 10))
            XCTAssertFalse(app.buttons["添加书籍"].isHittable)
            app.alerts.buttons["取消"].tap()
            XCTAssertTrue(waitUntil { !self.app.alerts.firstMatch.exists })
        }
    }

    /// The host runner compares the CoreSimulator captures: the SwiftUI paper
    /// around the page must use the same colour as the page rendered by C#.
    func testReaderPaperColorSurroundsThePage() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        for paper in ["米", "黑", "白"] {
            if !typography.exists { center.tap() }
            XCTAssertTrue(typography.waitForExistence(timeout: 10))
            typography.tap()
            let picker = app.segmentedControls["typography.paper"]
            XCTAssertTrue(picker.waitForExistence(timeout: 10))
            picker.buttons[paper].tap()
            XCTAssertTrue(picker.buttons[paper].isSelected)
            app.buttons["typography.done"].tap()
            XCTAssertTrue(waitUntil { !picker.exists })
            center.tap()
            XCTAssertTrue(waitUntil { !typography.exists })
            attachScreenshot("纸张-\(paper)")
        }
    }

    func testReaderTapOutsideSelectionDismissesItWithoutOpeningToolbar() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let window = app.windows.firstMatch
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        let highlight = app.buttons["reader.划线"]
        selectText()
        attachScreenshot("长按拖动选中正文")
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.62)).tap()
        let dismissed = waitUntil { !highlight.exists }
        attachScreenshot("点击正文取消选中")
        XCTAssertTrue(dismissed, "Tapping the page should dismiss the selection toolbar.")
        XCTAssertFalse(typography.exists, "The tap that dismisses a selection must not also open the reader toolbar.")
    }

    /// The materials tab was loaded before the reader added the highlight, and an
    /// EPUB highlight shows its date instead of a file path inside the book.
    func testNewHighlightAppearsInMaterialsWithReadableLocation() throws {
        app.tabBars.buttons["阅读资料"].tap()
        XCTAssertTrue(app.staticTexts["page.materials"].waitForExistence(timeout: 15))
        app.tabBars.buttons["书库"].tap()
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let window = app.windows.firstMatch
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        selectText()
        let highlight = app.buttons["reader.划线"]
        highlight.tap()
        XCTAssertTrue(waitUntil { !highlight.exists }, "Saving a highlight should close the selection toolbar.")
        attachScreenshot("添加划线")
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.back"].waitForExistence(timeout: 15))
        app.buttons["book.back"].tap()

        app.tabBars.buttons["阅读资料"].tap()
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        book.tap()
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyy/MM/dd"
        let today = "划线 · " + formatter.string(from: Date())
        let entries = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'"))
        let added = entries.matching(NSPredicate(format: "label CONTAINS %@", today)).firstMatch
        XCTAssertTrue(added.waitForExistence(timeout: 10), "The new highlight should be listed.\n\(app.debugDescription)")
        attachScreenshot("阅读资料新划线")
        XCTAssertEqual(entries.matching(NSPredicate(format: "label CONTAINS[c] 'html'")).count, 0,
                       "Material locations must not show EPUB file paths.")
    }

    func testReaderTocOpensAtTheCurrentChapter() throws {
        try openBook(containing: "Native UI EPUB")
        let toc = app.buttons["reader.目录"]
        XCTAssertTrue(toc.waitForExistence(timeout: 40))
        let position = app.staticTexts["reader.position"]
        let before = position.label
        app.sliders["reader.progress"].adjust(toNormalizedSliderPosition: 0.95)
        XCTAssertTrue(waitUntil { position.label != before })
        toc.tap()
        XCTAssertTrue(app.navigationBars["目录"].waitForExistence(timeout: 10))
        attachScreenshot("目录打开位置")
        let current = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.toc.' AND selected == true")).firstMatch
        XCTAssertTrue(current.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(waitUntil { current.isHittable }, "The table of contents should open with the current chapter visible.")
        current.tap()
        XCTAssertTrue(waitUntil { !self.app.navigationBars["目录"].exists })
    }

    private func openBook(containing title: String) throws {
        let booksMode = app.buttons["library.books"]
        if booksMode.exists && !booksMode.isSelected { booksMode.tap() }
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", title)).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), "Expected a local test book containing \(title).\n\(app.debugDescription)")
        book.tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        attachScreenshot("书籍详情")
        app.buttons["book.read"].tap()
    }

    /// Long-presses and drags across the first text lines. Illustration pages
    /// have no text, so it turns pages until the selection toolbar appears.
    private func selectText() {
        let window = app.windows.firstMatch
        let highlight = app.buttons["reader.划线"]
        // Start on the first text line at the left margin, where every text page has glyphs.
        let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.1, dy: 0.13))
        let end = window.coordinate(withNormalizedOffset: CGVector(dx: 0.6, dy: 0.2))
        var selected = false
        for _ in 0..<6 where !selected {
            start.press(forDuration: 1.0, thenDragTo: end)
            selected = highlight.waitForExistence(timeout: 5)
            if !selected { window.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5)).tap() }
        }
        XCTAssertTrue(selected, "Long-press and drag should select text.\n\(app.debugDescription)")
    }

    private func attachScreenshot(_ name: String) {
        print("KKINDLE_VISUAL_CHECKPOINT:\(name)")
        // The host runner can capture through simctl when XCTest screenshots
        // are black on machines with nonstandard simulator graphics drivers.
        RunLoop.current.run(until: Date().addingTimeInterval(2))
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func tapAccountElement(_ element: XCUIElement) {
        XCTAssertTrue(waitUntil {
            let savePrompt = self.app.staticTexts.matching(NSPredicate(
                format: "label CONTAINS[c] '保存密码' OR label CONTAINS[c] 'Save Password'")).firstMatch
            if savePrompt.exists {
                let later = self.app.buttons.matching(NSPredicate(
                    format: "label IN {'以后', '稍后', '以后再说', 'Not Now'}")).firstMatch
                if later.exists && later.isHittable { later.tap() }
                return false
            }
            return element.exists && element.isEnabled && element.isHittable
        }, "Account control must be ready after any system password-saving prompt.")
        element.tap()
    }

    private func dismissKeyboard() {
        if app.toolbars.buttons["完成"].exists { app.toolbars.buttons["完成"].tap() }
        else if app.buttons["完成"].exists { app.buttons["完成"].firstMatch.tap() }
        XCTAssertTrue(waitUntil { !self.app.keyboards.firstMatch.exists })
    }

    private func replaceText(_ element: XCUIElement, with text: String) {
        element.tap()
        if let current = element.value as? String, current != element.placeholderValue, !current.isEmpty {
            element.coordinate(withNormalizedOffset: CGVector(dx: 0.95, dy: 0.5)).tap()
            element.typeText(String(repeating: XCUIKeyboardKey.delete.rawValue, count: current.count))
        }
        element.typeText(text)
    }

    private func reveal(_ element: XCUIElement) {
        for _ in 0..<5 {
            if element.exists && element.isHittable && element.frame.maxY < app.frame.maxY - 72 { return }
            let typography = app.scrollViews["typography.scroll"]
            if typography.exists { typography.swipeUp() }
            else { app.swipeUp() }
        }
    }

    private func assertCompactTypographyPanel() {
        let panel = app.otherElements["typography.panel"]
        XCTAssertTrue(panel.waitForExistence(timeout: 5))
        XCTAssertGreaterThan(panel.frame.minY, app.frame.minY + app.frame.height * 0.5,
                             "Reader settings must leave at least the upper half of the page visible: \(panel.frame)")
    }

    private func waitUntil(_ predicate: @escaping () -> Bool) -> Bool {
        let expectation = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in predicate() }, object: nil)
        return XCTWaiter.wait(for: [expectation], timeout: 10) == .completed
    }
}
