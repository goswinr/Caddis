#!/usr/bin/env node
// Caddis as an extension of Claude Desktop. Claude Desktop runs it with its own Node and talks MCP to it over stdio,
// one JSON-RPC message per line. It relays each message to the MCP endpoint of Caddis in Rhino or Revit on this computer,
// which answers every request with a single JSON response, so a message is one POST.
// Arguments: the port of Caddis, and the name of the app, e.g. 7810 Rhino. No dependencies, Node 18 or later for fetch.
'use strict'

const fs = require('fs')
const path = require('path')
const readline = require('readline')

const port = Number(process.argv[2]) || 7810
const app = process.argv[3] || 'the app'
const url = `http://127.0.0.1:${port}/mcp`

// What Caddis offered when the extension was made: its MCP name and version, and, if it was made in the Caddis panel,
// its instructions and tools. They are shown while Caddis isn't running, so that the tools are listed anyway.
let offer = { name: 'caddis', version: '0.0.0', instructions: '', tools: [] }
try {
  offer = { ...offer, ...JSON.parse(fs.readFileSync(path.join(__dirname, 'caddis.json'), 'utf8')) }
} catch (e) {
  log(`no caddis.json: ${e.message}`)
}

const notRunning =
  `Caddis in ${app} doesn't answer at ${url}. Ask the person to start ${app} and the server in its Caddis panel, ` +
  `or to check the port in the settings of this extension in Claude Desktop.`

/** The Mcp-Session-Id that Caddis handed out on initialize. Caddis doesn't check it, it tells it that a session moved. */
let session = null
/** The initialize request of Claude Desktop, to start a session with Caddis when it wasn't running at first. */
let initialize = null
/** While the tools were listed without Caddis: polls it, and tells Claude Desktop once it answers. */
let watch = null

function log(text) {
  process.stderr.write(`caddis relay: ${text}\n`)
}

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n')
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

/** POSTs a message to Caddis. Returns its status and JSON body, null for 202. Throws if nothing answers. */
async function post(message) {
  const headers = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' }
  if (session) headers['Mcp-Session-Id'] = session
  // Retries for a moment: while another instance of the app takes over the port, nothing listens or Caddis answers 503.
  for (let attempt = 1; ; attempt++) {
    try {
      const response = await fetch(url, { method: 'POST', headers, body: JSON.stringify(message) })
      if (response.status === 503 && attempt < 10) {
        await sleep(300)
        continue
      }
      const id = response.headers.get('mcp-session-id')
      if (id) session = id
      const text = await response.text()
      return { status: response.status, body: text ? JSON.parse(text) : null }
    } catch (e) {
      if (attempt >= 3) throw e
      await sleep(300)
    }
  }
}

/** The answer to a request while Caddis doesn't answer. */
function offline(message) {
  const result = (r) => ({ jsonrpc: '2.0', id: message.id, result: r })
  switch (message.method) {
    case 'initialize':
      return result({
        protocolVersion: (message.params && message.params.protocolVersion) || '2025-06-18',
        capabilities: { tools: { listChanged: true } },
        serverInfo: { name: offer.name, title: 'Caddis', version: offer.version },
        instructions: offer.instructions ? `${offer.instructions}\n\n${notRunning}` : notRunning,
      })
    case 'ping':
      return result({})
    case 'tools/list':
      startWatching()
      return result({
        tools: offer.tools.length
          ? offer.tools
          : [{ name: 'caddis_status', description: `State of Caddis in ${app}. ${notRunning}`, inputSchema: { type: 'object', properties: {} } }],
      })
    case 'tools/call':
      return result({ content: [{ type: 'text', text: notRunning }], isError: true })
    default:
      return { jsonrpc: '2.0', id: message.id, error: { code: -32000, message: notRunning } }
  }
}

/** Polls Caddis while it doesn't answer. Once it does, starts a session if there is none and asks Claude Desktop to list the tools again. */
function startWatching() {
  if (watch) return
  watch = setInterval(async () => {
    try {
      if (!session && initialize) await post(initialize)
      else await post({ jsonrpc: '2.0', id: 'caddis-relay-ping', method: 'ping' })
      clearInterval(watch)
      watch = null
      log(`Caddis answers at ${url}`)
      send({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' })
    } catch {
      // still not running
    }
  }, 5000)
}

async function handle(message) {
  const isRequest = message.id !== undefined && message.id !== null && typeof message.method === 'string'
  if (message.method === 'initialize') initialize = message
  let reply
  try {
    reply = await post(message)
  } catch (e) {
    log(`${message.method}: ${e.cause ? e.cause.message : e.message}`)
    if (isRequest) send(offline(message))
    return
  }
  const messages = reply.body === null ? [] : [].concat(reply.body)
  if (isRequest && !messages.some((m) => m && m.jsonrpc === '2.0')) {
    // not JSON-RPC, e.g. {error} of Caddis for a request from a web page, or another program on the port
    const text = reply.body && reply.body.error ? reply.body.error : `HTTP status ${reply.status}`
    send({ jsonrpc: '2.0', id: message.id, error: { code: -32000, message: `${text} (${url})` } })
    return
  }
  for (const m of messages) {
    // Caddis offers no list_changed, this relay sends it when Caddis starts after the tools were listed without it
    if (message.method === 'initialize' && m.result && m.result.capabilities) m.result.capabilities.tools = { listChanged: true }
    send(m)
  }
}

const lines = readline.createInterface({ input: process.stdin })
lines.on('line', (line) => {
  if (!line.trim()) return
  let parsed
  try {
    parsed = JSON.parse(line)
  } catch (e) {
    send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: `Parse error: ${e.message}` } })
    return
  }
  for (const message of [].concat(parsed)) handle(message)
})
lines.on('close', () => process.exit(0))
