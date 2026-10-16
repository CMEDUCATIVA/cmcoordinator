import * as THREE from 'three';

export class MaterialPool {
  constructor() { this.materials = new Map(); }
  acquire(mesh) {
    const key = `${mesh.color}/${mesh.opacity}`;
    let entry = this.materials.get(key);
    if (!entry) {
      entry = { count: 0, material: new THREE.MeshStandardMaterial({ color: mesh.color, roughness: 0.75,
        side: THREE.DoubleSide, transparent: mesh.opacity < 1, opacity: mesh.opacity, depthWrite: mesh.opacity >= 1 }) };
      this.materials.set(key, entry);
    }
    entry.count++;
    return { key, material: entry.material };
  }
  release(key) {
    const entry = this.materials.get(key);
    if (entry && --entry.count === 0) { entry.material.dispose(); this.materials.delete(key); }
  }
}
