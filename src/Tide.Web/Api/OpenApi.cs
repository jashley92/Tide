using System.Text.Json;

namespace Tide.Web.Api;

/// <summary>The API's OpenAPI 3.1 description, served at <c>/api/v1/openapi.json</c>. Kept by hand next to the endpoints.</summary>
public static class OpenApi
{
    // The server as the document is written; served, it's the API's address under the app's path base.
    private const string Servers = "\"servers\": [ { \"url\": \"/api/v1\" } ]";

    /// <summary>
    /// The description as served to a request under <paramref name="pathBase"/>: its server is the API's address there
    /// (<c>/tide/api/v1</c> under an IIS sub-path). "/api/v1" alone names the site's root, where a client built from the
    /// description would call another application, or nothing.
    /// </summary>
    public static string For(PathString pathBase) =>
        Document.Replace(Servers, "\"servers\": [ { \"url\": " + JsonSerializer.Serialize(pathBase.Add("/api/v1").Value) + " } ]", StringComparison.Ordinal);

    public const string Document = """
        {
          "openapi": "3.1.0",
          "info": {
            "title": "TIDE API",
            "version": "1",
            "description": "Read briefs, actions (with their files), Our stack and metrics; close actions, enter metric values and propose triage items, and push detection rules for Coverage. Sign in with an API token (Authorization: Bearer tide_pat_...) or Windows. A token is held to its scopes and TLP ceiling. Every write is audited under the caller's name (a detection push as one entry with its counts)."
          },
          "servers": [ { "url": "/api/v1" } ],
          "security": [ { "token": [] }, { "windows": [] } ],
          "components": {
            "securitySchemes": {
              "token": { "type": "http", "scheme": "bearer", "description": "An API token issued under Administration > API access." },
              "windows": { "type": "http", "scheme": "negotiate" }
            },
            "schemas": {
              "Problem": { "type": "object", "properties": { "status": { "type": "integer" }, "detail": { "type": "string" } } },
              "Brief": {
                "type": "object",
                "properties": {
                  "day": { "type": "string", "format": "date" },
                  "status": { "type": "string", "enum": ["Draft", "Ready", "Presented", "Sent"] },
                  "finishedAs": {
                    "type": ["string", "null"], "enum": ["emailed", "distributed", "notSent", null],
                    "description": "How a Sent brief was finished: emailed, marked distributed (email off), or finished as not sent. One emailed after either reads emailed. Null until it's Sent."
                  },
                  "ceiling": { "type": "string" }, "marking": { "type": "string" }, "intro": { "type": ["string", "null"] },
                  "articles": { "type": "array", "items": { "type": "object" } }, "withheldArticles": { "type": "integer" }
                }
              },
              "Action": {
                "type": "object",
                "properties": {
                  "number": { "type": "integer" }, "title": { "type": "string" }, "kind": { "type": "string" }, "priority": { "type": "string" },
                  "status": { "type": "string" }, "outcome": { "type": ["string", "null"] }, "team": { "type": "string" },
                  "assignee": { "type": ["string", "null"] }, "dueDate": { "type": ["string", "null"], "format": "date" },
                  "closedDate": { "type": ["string", "null"], "format": "date" }, "articleId": { "type": ["string", "null"], "format": "uuid" },
                  "files": {
                    "type": ["array", "null"], "items": { "$ref": "#/components/schemas/ActionFile" },
                    "description": "The action's live files (added to it, and attached to its comments), newest first. Only on /actions/{number}; null in a list."
                  }
                }
              },
              "ActionFile": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "format": "uuid" }, "name": { "type": "string" }, "size": { "type": "integer", "description": "Bytes" },
                  "sha256": { "type": ["string", "null"], "description": "Lower-case hex SHA-256 of the bytes, recorded as they arrived" },
                  "contentType": { "type": "string", "description": "What its bytes are: an image's type (image/png, image/jpeg, image/gif, image/webp), else application/octet-stream" },
                  "declaredType": { "type": ["string", "null"], "description": "The type the uploader's browser declared, when recorded (never trusted)" },
                  "addedBy": { "type": "string" }, "addedAt": { "type": "string", "format": "date-time" },
                  "description": { "type": ["string", "null"], "maxLength": 500 },
                  "source": { "type": "string", "enum": ["action", "comment"], "description": "Added to the action, or attached to one of its comments" },
                  "commentId": { "type": ["string", "null"], "format": "uuid" },
                  "risky": { "type": "boolean", "description": "It can run, or carry something that runs, by its name, declared type or first bytes" },
                  "riskReason": { "type": ["string", "null"] },
                  "downloadUrl": { "type": "string", "description": "Under the API: /actions/{number}/files/{fileId}, with the same scope and ceiling" }
                }
              },
              "ActionUpdate": {
                "type": "object", "required": ["status"],
                "properties": {
                  "status": { "type": "string", "enum": ["Open", "InProgress", "Blocked", "Done", "Cancelled", "NotApplicable"] },
                  "outcome": { "type": "string", "description": "Needed to close it Done: one of its kind's outcomes." },
                  "note": { "type": "string", "maxLength": 2000 }
                }
              },
              "MetricEntry": {
                "type": "object", "required": ["key", "day", "value"],
                "properties": { "key": { "type": "string" }, "day": { "type": "string", "format": "date" }, "value": { "type": "number" }, "note": { "type": "string" } }
              },
              "Proposal": {
                "type": "object", "required": ["title", "url"],
                "properties": { "title": { "type": "string", "maxLength": 500 }, "url": { "type": "string", "format": "uri" }, "summary": { "type": "string", "maxLength": 2000 } }
              }
            }
          },
          "paths": {
            "/briefs/{day}": {
              "get": {
                "summary": "A brief as the team edition shows it, cut at the lower of the team edition's ceiling and the token's. Scope: briefs:read.",
                "parameters": [ { "name": "day", "in": "path", "required": true, "schema": { "type": "string" }, "description": "yyyy-MM-dd, or today" } ],
                "responses": { "200": { "description": "The brief", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Brief" } } } }, "404": { "description": "No brief that day" } }
              }
            },
            "/actions": {
              "get": {
                "summary": "Actions: open (the default), overdue, or closed in the last 90 days. For a token, one raised from an article above the lower of the team edition's ceiling and the token's is left out. Scope: actions:read.",
                "parameters": [ { "name": "view", "in": "query", "schema": { "type": "string", "enum": ["open", "overdue", "closed"] } } ],
                "responses": { "200": { "description": "The actions", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Action" } } } } } }
              }
            },
            "/actions/{number}": {
              "get": {
                "summary": "One action, by its number (A-21 is 21), with its live files; 404 for a token when its article is above the ceiling, as in the list. Scope: actions:read.",
                "parameters": [ { "name": "number", "in": "path", "required": true, "schema": { "type": "integer" } } ],
                "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Action" } } }, "description": "The action" }, "404": { "description": "No such action" } }
              },
              "patch": {
                "summary": "Change an action's status, with a note (closing it Done needs an outcome). Nothing else changes. Scope: actions:write.",
                "parameters": [ { "name": "number", "in": "path", "required": true, "schema": { "type": "integer" } } ],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ActionUpdate" } } } },
                "responses": { "200": { "description": "The action, changed" }, "400": { "description": "A bad status or outcome" }, "404": { "description": "No such action" } }
              }
            },
            "/actions/{number}/files/{fileId}": {
              "get": {
                "summary": "Download a live file on the action or one of its comments (fileId is a file's id from the action's files): as it was added, an attachment of type application/octet-stream, or with zip=true inside a zip with the password infected (classic ZipCrypto, which any zip tool opens). Held to the same scope and ceiling as the action. Scope: actions:read.",
                "parameters": [
                  { "name": "number", "in": "path", "required": true, "schema": { "type": "integer" } },
                  { "name": "fileId", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } },
                  { "name": "zip", "in": "query", "schema": { "type": "boolean" }, "description": "true: in a zip with the password infected" }
                ],
                "responses": {
                  "200": { "description": "The file", "content": { "application/octet-stream": {}, "application/zip": {} } },
                  "404": { "description": "No such action for the caller, or no such live file on it" }
                }
              }
            },
            "/stack": {
              "get": {
                "summary": "Our stack: products, versions and counts. Notes (internal, AMBER) only for a token whose ceiling allows AMBER. Scope: stack:read.",
                "responses": { "200": { "description": "The products" } }
              }
            },
            "/metrics": {
              "get": {
                "summary": "The opening metrics and their values between two days (the last 30 by default). Scope: metrics:read.",
                "parameters": [ { "name": "from", "in": "query", "schema": { "type": "string", "format": "date" } }, { "name": "to", "in": "query", "schema": { "type": "string", "format": "date" } } ],
                "responses": { "200": { "description": "The metrics and values" } }
              },
              "post": {
                "summary": "Enter a day's value for a metric, by its key, as typing it on the Metrics page would. Scope: metrics:write.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/MetricEntry" } } } },
                "responses": { "204": { "description": "Entered" }, "404": { "description": "No metric with that key" } }
              }
            },
            "/triage": {
              "post": {
                "summary": "Propose an item for triage, under a source named after the caller. An analyst still decides it. Scope: triage:propose.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Proposal" } } } },
                "responses": { "201": { "description": "Proposed" }, "200": { "description": "That address was already in triage" } }
              }
            },
            "/detections": {
              "post": {
                "summary": "Push the full set of detection rules the caller manages; it replaces the caller's last push and no other caller's (a token's by its name, so one issued again under the same name carries on). A push stands until the next one, however long that is; the set stops counting only when its token is revoked or expires and no token of the same name is active. Only names and ATT&CK tags are kept, for Reports > Coverage. Scope: detections:write.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": ["rules"],
                  "properties": { "rules": { "type": "array", "items": { "type": "object", "required": ["name"], "properties": {
                    "id": { "type": "string" }, "name": { "type": "string" }, "techniques": { "type": "array", "items": { "type": "string" } },
                    "platform": { "type": "string" }, "enabled": { "type": "boolean" } } } } } } } } },
                "responses": { "200": { "description": "Stored: added, changed, unchanged, notInImport and problems" } }
              }
            }
          },
          "webhooks": {
            "event": {
              "post": {
                "summary": "action.raised, action.overdue or brief.sent. Signed: X-Tide-Signature is sha256= and the HMAC-SHA256, with the webhook's secret, of the X-Tide-Timestamp value, a dot and the body. The body's id (also X-Tide-Event-Id) is the same each time an event is posted again, so a repeat can be dropped. An action's data carries marking, the TLP of the article it was raised from (null when none); an action raised from an article above the team edition's ceiling is never posted.",
                "responses": { "200": { "description": "Received" } }
              }
            }
          }
        }
        """;
}
