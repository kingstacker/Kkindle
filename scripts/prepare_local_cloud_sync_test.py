#!/usr/bin/env python3
"""Provision dedicated fictional Plus accounts on the two loopback demo servers.

Never uses production URLs or modifies retained demo accounts. The resulting
0600 JSON file is consumed by the opt-in LiveCloudSyncPrivacyTests; credentials
and responses are never printed. No real payment is made.
"""
import argparse
import html
import http.cookiejar
import json
import os
from pathlib import Path
import re
import secrets
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, url):
        return None


class LocalRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, origin):
        super().__init__()
        self.origin = origin

    def redirect_request(self, request, fp, code, message, headers, url):
        parsed = urllib.parse.urlsplit(url)
        if f"{parsed.scheme}://{parsed.netloc}" != self.origin:
            raise RuntimeError("本机管理页面发生跨服务器重定向，已停止验证。")
        return super().redirect_request(request, fp, code, message, headers, url)


class LocalClient:
    def __init__(self, port, region):
        if not isinstance(port, int) or not 1024 <= port <= 65535:
            raise RuntimeError("本地服务端口无效。")
        self.origin = f"http://127.0.0.1:{port}"
        self.region = region
        self.api = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        self.admin = urllib.request.build_opener(urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), LocalRedirect(self.origin))

    def request(self, method, suffix, body=None, bearer=None):
        headers = {"X-Kkindle-Region": self.region}
        if bearer:
            headers["Authorization"] = "Bearer " + bearer
        encoded = None if body is None else json.dumps(body).encode()
        if encoded is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.origin + suffix, encoded, headers, method=method)
        try:
            with self.api.open(request, timeout=30) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            return error.code, b""  # Do not retain or print credential-bearing responses.

    def form(self, page, values, handler=None):
        with self.admin.open(self.origin + page, timeout=30) as response:
            document = response.read().decode()
        match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', document)
        if not match:
            raise RuntimeError("本机管理表单缺少 CSRF 字段。")
        values = {**values, "__RequestVerificationToken": html.unescape(match.group(1))}
        request = urllib.request.Request(self.origin + page + ("?handler=" + handler if handler else ""),
            urllib.parse.urlencode(values).encode(), {"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
        with self.admin.open(request, timeout=30) as response:
            return html.unescape(response.read().decode())


def required(status, expected, phase):
    if status != expected:
        raise RuntimeError(f"{phase}失败（HTTP {status}）；凭据与响应未输出。")


def save_private(path, manifest):
    temporary = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w") as output:
        json.dump(manifest, output, ensure_ascii=False, indent=2)
        output.write("\n")
    os.replace(temporary, path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-config", type=Path,
        default=Path.home() / "Desktop/Kkindle_server/.local/local-config.json")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise RuntimeError("输出文件已经存在；请指定新的被忽略的本机测试配置文件。")
    config = json.loads(args.server_config.read_text())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    manifest = {"version": 1, "regions": {}}
    save_private(args.output, manifest)
    for slug, number in [("china", 0), ("international", 1)]:
        region = config["regions"][slug]
        client = LocalClient(region["port"], slug)
        status, body = client.request("GET", "/health")
        required(status, 200, "本机服务检查")
        health = json.loads(body)
        if health.get("region") != number or health.get("administration") is not True:
            raise RuntimeError("目标不是预期区域的本机管理服务。")
        client.form("/admin/login", {"Username": region["adminUsername"], "Password": region["adminPassword"]})
        with client.admin.open(client.origin + "/admin/orders", timeout=30) as response:
            if "当前为本地演示" not in response.read().decode():
                raise RuntimeError("目标未开启本地演示，不会创建测试订单。")
        user = {"username": "privacy_" + ("cn_" if number == 0 else "intl_") + secrets.token_hex(4),
                "password": secrets.token_urlsafe(24), "serverUrl": client.origin}
        status, body = client.request("POST", "/api/accounts/register", {
            "region": number, "username": user["username"], "password": user["password"],
            "confirmPassword": user["password"], "email": user["username"] + "@example.invalid",
            "deviceId": str(uuid.uuid4()), "deviceName": "虚构同步验证初始化设备", "platform": "LocalSyncTest"})
        required(status, 201, "独立测试账号注册")
        session = json.loads(body)
        user["id"] = session["user"]["id"]
        manifest["regions"][slug] = user
        save_private(args.output, manifest)
        status, body = client.request("POST", "/api/accounts/orders", {
            "productId": "plus-365d", "requestId": str(uuid.uuid4())}, session["token"])
        required(status, 201, "本机 Plus 演示订单")
        order = json.loads(body)
        page = client.form("/admin/orders", {"OrderId": order["id"], "PaymentConfirmed": "true",
            "Reason": "本地虚构加密同步验证，不涉及真实付款"}, "Fulfill")
        if "订单已到账" not in page:
            raise RuntimeError("本机 Plus 演示订单未确认到账。")
        user["orderId"] = order["id"]
        user["complete"] = True
        save_private(args.output, manifest)
        print(slug + "：独立虚构 Plus 测试账号已准备（未修改演示账号）。")
    print("本机测试配置：" + str(args.output.resolve()) + "（0600；不提交；不输出密码）。")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Network/JSON exceptions can contain URLs or bodies. Only deliberately
        # sanitized validation errors are allowed in output.
        print(str(error) if isinstance(error, RuntimeError) else "本机测试准备失败；凭据与响应未输出。", file=sys.stderr)
        sys.exit(1)
