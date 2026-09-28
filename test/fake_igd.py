"""A pretend UPnP router for UpnpCheck: answers SSDP searches and the WANIPConnection SOAP calls."""
import socket, struct, sys, threading
from http.server import BaseHTTPRequestHandler, HTTPServer

HTTP_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 49001
EXTERNAL_IP = sys.argv[2] if len(sys.argv) > 2 else "203.0.113.7"
calls = []

DESC = f"""<?xml version="1.0"?>
<root xmlns="urn:schemas-upnp-org:device-1-0"><device>
 <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
 <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
  <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
   <serviceList><service>
    <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
    <controlURL>/ctl/IPConn</controlURL>
   </service></serviceList>
  </device></deviceList></device></deviceList>
</device></root>"""

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        body = DESC.encode()
        self.send_response(200); self.send_header("Content-Type", "text/xml"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def do_POST(self):
        data = self.rfile.read(int(self.headers["Content-Length"])).decode()
        action = self.headers.get("SOAPAction", "").strip('"').split("#")[-1]
        calls.append((action, data))
        print("SOAP", action, flush=True)
        if action == "AddPortMapping" and "<NewLeaseDuration>0<" in data:
            # Like some real routers: permanent mappings refused, leases accepted.
            body = b'<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><detail><UPnPError><errorCode>725</errorCode><errorDescription>OnlyPermanentLeasesSupported</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>'
            self.send_response(500)
        else:
            inner = f"<NewExternalIPAddress>{EXTERNAL_IP}</NewExternalIPAddress>" if action == "GetExternalIPAddress" else ""
            body = f'<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:{action}Response xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">{inner}</u:{action}Response></s:Body></s:Envelope>'.encode()
            self.send_response(200)
        self.send_header("Content-Type", "text/xml"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)

def ssdp():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("", 1900))
    s.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, struct.pack("4sl", socket.inet_aton("239.255.255.250"), socket.INADDR_ANY))
    while True:
        data, addr = s.recvfrom(2048)
        if b"M-SEARCH" in data:
            print("SSDP search from", addr[0], flush=True)
            reply = (f"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n"
                     f"LOCATION: http://127.0.0.1:{HTTP_PORT}/rootDesc.xml\r\nSERVER: fake\r\nEXT:\r\n\r\n").encode()
            s.sendto(reply, addr)

threading.Thread(target=ssdp, daemon=True).start()
print("fake IGD ready", flush=True)
HTTPServer(("127.0.0.1", HTTP_PORT), Handler).serve_forever()
