from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json


HOST = "127.0.0.1"
PORT = 8765

state = {
    "mode": "focus",
    "ai_assist": "off",
}


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/input-event":
            self.send_response(404)
            self.end_headers()
            return

        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length)

        try:
            event = json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError:
            self.send_response(400)
            self.end_headers()
            return

        event_type = event.get("type")
        payload = event.get("payload", {})

        if event_type == "CHANGE_MODE":
            state["mode"] = payload.get(
                "mode",
                state["mode"],
            )

        elif event_type == "AI_ASSIST_OFF":
            state["ai_assist"] = "off"

        elif event_type == "AI_ASSIST_SUGGESTION":
            state["ai_assist"] = "suggestion"

        elif event_type == "AI_ASSIST_ON":
            state["ai_assist"] = "on"

        print()
        print("Received InputEvent")
        print(json.dumps(event, indent=2))
        print()
        print("Mock state")
        print(json.dumps(state, indent=2))

        response = json.dumps({
            "ok": True,
            "state": state,
        }).encode("utf-8")

        self.send_response(200)
        self.send_header(
            "Content-Type",
            "application/json",
        )
        self.send_header(
            "Content-Length",
            str(len(response)),
        )
        self.end_headers()
        self.wfile.write(response)

    def log_message(self, format, *args):
        return


if __name__ == "__main__":
    server = ThreadingHTTPServer(
        (HOST, PORT),
        Handler,
    )

    print(
        f"CluckIn mock receiver listening on "
        f"http://{HOST}:{PORT}/input-event"
    )

    server.serve_forever()