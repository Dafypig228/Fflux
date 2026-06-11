import * as vscode from 'vscode';
import * as http from 'http';

const DAVOS_URL = 'http://localhost:27834/';
const DEBOUNCE_MS = 1000; // avoid flooding on every keystroke

let debounceTimer: NodeJS.Timeout | undefined;

/**
 * Sends current VS Code editor state to the Davos HTTP bridge.
 * Payload matches the VsCodeState record in ChromeBridgeService.cs:
 *   { source, file, language, cursorLine, selectedText, visibleText, errors, warnings }
 */
async function sendContext(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;

    const doc       = editor.document;
    const cursor    = editor.selection.active;
    const selected  = editor.document.getText(editor.selection);

    // Visible text: up to 50 lines around cursor
    const startLine = Math.max(0, cursor.line - 25);
    const endLine   = Math.min(doc.lineCount - 1, cursor.line + 25);
    const visibleText = doc.getText(new vscode.Range(startLine, 0, endLine, 10000));

    // Diagnostic counts for this file
    const diags    = vscode.languages.getDiagnostics(doc.uri);
    const errors   = diags.filter(d => d.severity === vscode.DiagnosticSeverity.Error).length;
    const warnings = diags.filter(d => d.severity === vscode.DiagnosticSeverity.Warning).length;

    const payload = JSON.stringify({
        source:       'vscode',
        file:         doc.fileName,
        language:     doc.languageId,
        cursorLine:   cursor.line + 1,   // 1-based
        selectedText: selected || null,
        visibleText:  visibleText.length > 3000
                        ? visibleText.substring(0, 3000) + '…'
                        : visibleText,
        errors,
        warnings
    });

    return new Promise<void>((resolve) => {
        try {
            const req = http.request(DAVOS_URL, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Content-Length': Buffer.byteLength(payload)
                }
            }, () => resolve());
            req.on('error', () => resolve()); // Davos not running — fail silently
            req.write(payload);
            req.end();
        } catch {
            resolve();
        }
    });
}

function scheduleContext(): void {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
        sendContext().catch(() => {});
    }, DEBOUNCE_MS);
}

export function activate(ctx: vscode.ExtensionContext): void {
    // Send on file open/switch
    ctx.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(() => scheduleContext())
    );

    // Send on text edit (debounced)
    ctx.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(() => scheduleContext())
    );

    // Send immediately on save
    ctx.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument(() => sendContext().catch(() => {}))
    );

    // Send on diagnostics change (errors/warnings updated)
    ctx.subscriptions.push(
        vscode.languages.onDidChangeDiagnostics(() => scheduleContext())
    );

    // Send on cursor move (debounced — tells Davos where user is working)
    ctx.subscriptions.push(
        vscode.window.onDidChangeTextEditorSelection(() => scheduleContext())
    );

    // Send initial state
    scheduleContext();
    console.log('[Davos] VS Code Bridge active — sending context to localhost:27834');
}

export function deactivate(): void {
    if (debounceTimer) clearTimeout(debounceTimer);
}
