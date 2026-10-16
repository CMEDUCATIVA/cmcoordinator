export class SceneState {
  constructor() { this.elements = new Map(); this.sessionId = null; this.sequence = 0; }
  snapshot(state) {
    this.bim = state.bim;
    this.elements = new Map(state.elements.map(element => [element.id, element]));
    this.sessionId = state.sessionId;
    this.sequence = state.sequence;
    this.title = state.title;
    this.view = state.view;
    this.updatedAt = state.updatedAt;
  }
  patch(packet) {
    if (!packet.reset && (packet.sessionId !== this.sessionId || packet.baseSequence !== this.sequence)) return false;
    if (packet.sessionId === this.sessionId && packet.sequence <= this.sequence) return true;
    if (packet.reset) this.elements.clear();
    for (const id of packet.removed) this.elements.delete(id);
    for (const element of packet.updated) this.elements.set(element.id, element);
    this.sessionId = packet.sessionId;
    this.sequence = packet.sequence;
    this.title = packet.title;
    this.view = packet.view;
    this.updatedAt = new Date().toISOString();
    return true;
  }
}
