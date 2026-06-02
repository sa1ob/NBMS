#!/usr/bin/env python3
"""Minimal NBMS editor for Phase 5."""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import tkinter as tk
import zipfile
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bms2nbms.bms2nbms import convert as convert_bms, canonical_json_hash  # noqa: E402
from nbms_crypto import load_json, new_author_keypair, sha256_file, sign_header, write_json  # noqa: E402
from nbms_encrypt_audio.nbms_encrypt_audio import encrypt_archive  # noqa: E402
from nbms_pack.nbms_pack import create_package  # noqa: E402


class JsonTab:
    def __init__(self, notebook: ttk.Notebook, title: str, path: Path | None = None) -> None:
        self.path = path
        self.frame = ttk.Frame(notebook)
        self.text = tk.Text(self.frame, wrap="none", undo=True)
        y_scroll = ttk.Scrollbar(self.frame, orient="vertical", command=self.text.yview)
        x_scroll = ttk.Scrollbar(self.frame, orient="horizontal", command=self.text.xview)
        self.text.configure(yscrollcommand=y_scroll.set, xscrollcommand=x_scroll.set)
        self.text.grid(row=0, column=0, sticky="nsew")
        y_scroll.grid(row=0, column=1, sticky="ns")
        x_scroll.grid(row=1, column=0, sticky="ew")
        self.frame.rowconfigure(0, weight=1)
        self.frame.columnconfigure(0, weight=1)
        notebook.add(self.frame, text=title)

    def set_json(self, value: Any) -> None:
        self.text.delete("1.0", "end")
        self.text.insert("1.0", json.dumps(value, ensure_ascii=False, indent=2) + "\n")

    def get_json(self) -> Any:
        return json.loads(self.text.get("1.0", "end"))


def load_audio_manifest(base_dir: Path, header: dict[str, Any]) -> dict[str, Any]:
    audio_path = base_dir / header["audio"]["file"]
    with zipfile.ZipFile(audio_path) as archive:
        return json.loads(archive.read("manifest.json").decode("utf-8"))


def collect_chart_audio_refs(chart: dict[str, Any]) -> list[tuple[str, str, int | None]]:
    refs: list[tuple[str, str, int | None]] = []
    for index, note in enumerate(chart.get("notes", [])):
        audio_id = note.get("audioId")
        if audio_id:
            refs.append(("note", audio_id, index))
    for index, event in enumerate(chart.get("backgroundAudio", [])):
        audio_id = event.get("audioId")
        if audio_id:
            refs.append(("backgroundAudio", audio_id, index))
    return refs


def find_missing_audio_refs(charts: list[tuple[Path, dict[str, Any]]], audio_manifest: dict[str, Any]) -> list[dict[str, Any]]:
    available = {entry.get("audioId") for entry in audio_manifest.get("entries", [])}
    missing: list[dict[str, Any]] = []
    for path, chart in charts:
        for ref_type, audio_id, index in collect_chart_audio_refs(chart):
            if audio_id not in available:
                missing.append(
                    {
                        "chart": path.name,
                        "refType": ref_type,
                        "index": index,
                        "audioId": audio_id,
                    }
                )
    return missing


class AudioListTab:
    def __init__(self, notebook: ttk.Notebook) -> None:
        self.frame = ttk.Frame(notebook, padding=6)
        columns = ("audioId", "codec", "durationMs", "sampleRate", "channels", "encrypted", "path")
        self.tree = ttk.Treeview(self.frame, columns=columns, show="headings")
        for column in columns:
            self.tree.heading(column, text=column)
            self.tree.column(column, width=120 if column != "path" else 280, anchor="w")
        y_scroll = ttk.Scrollbar(self.frame, orient="vertical", command=self.tree.yview)
        x_scroll = ttk.Scrollbar(self.frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=y_scroll.set, xscrollcommand=x_scroll.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        y_scroll.grid(row=0, column=1, sticky="ns")
        x_scroll.grid(row=1, column=0, sticky="ew")
        self.frame.rowconfigure(0, weight=1)
        self.frame.columnconfigure(0, weight=1)
        notebook.add(self.frame, text="Audio")

    def set_manifest(self, manifest: dict[str, Any]) -> None:
        self.tree.delete(*self.tree.get_children())
        for entry in manifest.get("entries", []):
            self.tree.insert(
                "",
                "end",
                values=(
                    entry.get("audioId", ""),
                    entry.get("codec", ""),
                    entry.get("durationMs", ""),
                    entry.get("sampleRate", ""),
                    entry.get("channels", ""),
                    str(entry.get("encrypted", False)),
                    entry.get("path", ""),
                ),
            )


class ReferenceCheckTab:
    def __init__(self, notebook: ttk.Notebook) -> None:
        self.frame = ttk.Frame(notebook, padding=6)
        columns = ("chart", "refType", "index", "audioId")
        self.tree = ttk.Treeview(self.frame, columns=columns, show="headings")
        for column in columns:
            self.tree.heading(column, text=column)
            self.tree.column(column, width=150, anchor="w")
        self.summary = tk.StringVar(value="Run reference check after loading a project.")
        ttk.Label(self.frame, textvariable=self.summary, anchor="w").grid(row=0, column=0, sticky="ew", pady=(0, 6))
        self.tree.grid(row=1, column=0, sticky="nsew")
        self.frame.rowconfigure(1, weight=1)
        self.frame.columnconfigure(0, weight=1)
        notebook.add(self.frame, text="Reference Check")

    def set_missing(self, missing: list[dict[str, Any]]) -> None:
        self.tree.delete(*self.tree.get_children())
        if missing:
            self.summary.set(f"Missing audio references: {len(missing)}")
        else:
            self.summary.set("No missing audio references.")
        for item in missing:
            self.tree.insert("", "end", values=(item["chart"], item["refType"], item["index"], item["audioId"]))


class NoteTableTab:
    def __init__(self, notebook: ttk.Notebook, title: str, chart_tab: JsonTab) -> None:
        self.chart_tab = chart_tab
        self.frame = ttk.Frame(notebook, padding=6)
        columns = ("tick", "lane", "type", "audioId", "durationTicks")
        self.tree = ttk.Treeview(self.frame, columns=columns, show="headings", selectmode="browse")
        for column in columns:
            self.tree.heading(column, text=column)
            self.tree.column(column, width=130, anchor="w")
        self.tree.grid(row=0, column=0, columnspan=8, sticky="nsew", pady=(0, 6))
        self.tree.bind("<<TreeviewSelect>>", self.on_select)

        self.vars = {name: tk.StringVar() for name in columns}
        for index, column in enumerate(columns):
            ttk.Label(self.frame, text=column).grid(row=1, column=index, sticky="w")
            ttk.Entry(self.frame, textvariable=self.vars[column], width=16).grid(row=2, column=index, sticky="ew", padx=(0, 4))

        ttk.Button(self.frame, text="Reload JSON", command=self.reload_from_json).grid(row=2, column=5, padx=(8, 4))
        ttk.Button(self.frame, text="Add", command=self.add_note).grid(row=2, column=6, padx=(0, 4))
        ttk.Button(self.frame, text="Update", command=self.update_note).grid(row=2, column=7, padx=(0, 4))
        ttk.Button(self.frame, text="Delete", command=self.delete_note).grid(row=3, column=6, padx=(0, 4), pady=(6, 0))
        ttk.Button(self.frame, text="Apply to JSON", command=self.apply_to_json).grid(row=3, column=7, padx=(0, 4), pady=(6, 0))

        self.frame.rowconfigure(0, weight=1)
        self.frame.columnconfigure(0, weight=1)
        notebook.add(self.frame, text=title)
        self.reload_from_json()

    def reload_from_json(self) -> None:
        self.tree.delete(*self.tree.get_children())
        chart = self.chart_tab.get_json()
        for index, note in enumerate(chart.get("notes", [])):
            self.tree.insert(
                "",
                "end",
                iid=str(index),
                values=(
                    note.get("tick", ""),
                    note.get("lane", ""),
                    note.get("type", ""),
                    note.get("audioId", ""),
                    note.get("durationTicks", ""),
                ),
            )

    def on_select(self, _event: tk.Event) -> None:
        selected = self.tree.selection()
        if not selected:
            return
        values = self.tree.item(selected[0], "values")
        for name, value in zip(self.vars, values):
            self.vars[name].set(value)

    def values_to_note(self) -> dict[str, Any]:
        note: dict[str, Any] = {
            "tick": int(self.vars["tick"].get() or "0"),
            "lane": self.vars["lane"].get(),
            "type": self.vars["type"].get() or "tap",
        }
        audio_id = self.vars["audioId"].get()
        if audio_id:
            note["audioId"] = audio_id
        duration = self.vars["durationTicks"].get()
        if duration:
            note["durationTicks"] = int(duration)
        return note

    def add_note(self) -> None:
        note = self.values_to_note()
        self.tree.insert("", "end", values=(note.get("tick", ""), note.get("lane", ""), note.get("type", ""), note.get("audioId", ""), note.get("durationTicks", "")))

    def update_note(self) -> None:
        selected = self.tree.selection()
        if not selected:
            return
        note = self.values_to_note()
        self.tree.item(selected[0], values=(note.get("tick", ""), note.get("lane", ""), note.get("type", ""), note.get("audioId", ""), note.get("durationTicks", "")))

    def delete_note(self) -> None:
        for selected in self.tree.selection():
            self.tree.delete(selected)

    def table_notes(self) -> list[dict[str, Any]]:
        notes: list[dict[str, Any]] = []
        for row in self.tree.get_children():
            tick, lane, note_type, audio_id, duration = self.tree.item(row, "values")
            note: dict[str, Any] = {"tick": int(tick), "lane": lane, "type": note_type}
            if audio_id:
                note["audioId"] = audio_id
            if duration:
                note["durationTicks"] = int(duration)
            notes.append(note)
        notes.sort(key=lambda item: (item["tick"], item["lane"], item["type"]))
        return notes

    def apply_to_json(self) -> None:
        chart = self.chart_tab.get_json()
        chart["notes"] = self.table_notes()
        self.chart_tab.set_json(chart)


class NbmsEditor(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("NBMS Minimal Editor")
        self.geometry("1100x760")

        self.header_path: Path | None = None
        self.base_dir: Path | None = None
        self.header_tab: JsonTab | None = None
        self.chart_tabs: list[JsonTab] = []
        self.note_tabs: list[NoteTableTab] = []
        self.audio_tab: AudioListTab | None = None
        self.reference_tab: ReferenceCheckTab | None = None
        self.private_key: dict[str, Any] | None = None
        self.sign_on_save = tk.BooleanVar(value=False)

        self._build_ui()

    def _build_ui(self) -> None:
        toolbar = ttk.Frame(self, padding=6)
        toolbar.pack(side="top", fill="x")

        buttons = [
            ("Open NBMS", self.open_nbms),
            ("Save", self.save_normal),
            ("Save Encrypted Copy", self.save_encrypted_copy),
            ("Convert BMS", self.convert_bms_dialog),
            ("Package .nbmp", self.package_dialog),
            ("Refresh Views", self.refresh_views),
            ("Check Refs", self.check_references_dialog),
            ("Generate Key", self.generate_key_dialog),
            ("Load Key", self.load_key_dialog),
        ]
        for label, command in buttons:
            ttk.Button(toolbar, text=label, command=command).pack(side="left", padx=(0, 6))

        ttk.Checkbutton(toolbar, text="Sign on save", variable=self.sign_on_save).pack(side="left", padx=(12, 6))

        self.status = tk.StringVar(value="Open or convert an NBMS project.")
        ttk.Label(self, textvariable=self.status, anchor="w", padding=(6, 3)).pack(side="bottom", fill="x")

        self.notebook = ttk.Notebook(self)
        self.notebook.pack(side="top", fill="both", expand=True)

    def set_status(self, message: str) -> None:
        self.status.set(message)

    def reset_tabs(self) -> None:
        for tab_id in self.notebook.tabs():
            self.notebook.forget(tab_id)
        self.header_tab = None
        self.chart_tabs = []
        self.note_tabs = []
        self.audio_tab = None
        self.reference_tab = None

    def open_nbms(self) -> None:
        path = filedialog.askopenfilename(title="Open NBMS header", filetypes=[("NBMS header", "*.nbmh"), ("JSON", "*.json"), ("All files", "*.*")])
        if path:
            self.load_project(Path(path))

    def load_project(self, header_path: Path) -> None:
        try:
            header = load_json(header_path)
            self.header_path = header_path.resolve()
            self.base_dir = self.header_path.parent
            self.reset_tabs()

            self.header_tab = JsonTab(self.notebook, "Header", self.header_path)
            self.header_tab.set_json(header)

            for chart in header.get("charts", []):
                chart_path = self.base_dir / chart["file"]
                chart_tab = JsonTab(self.notebook, f"Chart: {chart.get('id', chart_path.name)}", chart_path)
                chart_tab.set_json(load_json(chart_path))
                self.chart_tabs.append(chart_tab)
                note_tab = NoteTableTab(self.notebook, f"Notes: {chart.get('id', chart_path.name)}", chart_tab)
                self.note_tabs.append(note_tab)

            self.audio_tab = AudioListTab(self.notebook)
            self.reference_tab = ReferenceCheckTab(self.notebook)
            self.refresh_views()

            self.set_status(f"Loaded {self.header_path}")
        except Exception as exc:
            messagebox.showerror("Open failed", str(exc))

    def read_editor_json(self) -> tuple[dict[str, Any], list[tuple[Path, dict[str, Any]]]]:
        if not self.header_tab or not self.header_path or not self.base_dir:
            raise ValueError("No NBMS project is loaded")
        header = self.header_tab.get_json()
        charts: list[tuple[Path, dict[str, Any]]] = []
        for tab in self.chart_tabs:
            if not tab.path:
                raise ValueError("Chart tab has no file path")
            charts.append((tab.path, tab.get_json()))
        return header, charts

    def apply_note_tables_to_json(self) -> None:
        for note_tab in self.note_tabs:
            note_tab.apply_to_json()

    def refresh_views(self) -> None:
        try:
            if not self.header_tab or not self.base_dir:
                return
            for note_tab in self.note_tabs:
                note_tab.reload_from_json()
            header, charts = self.read_editor_json()
            manifest = load_audio_manifest(self.base_dir, header)
            if self.audio_tab:
                self.audio_tab.set_manifest(manifest)
            if self.reference_tab:
                self.reference_tab.set_missing(find_missing_audio_refs(charts, manifest))
            self.set_status("Refreshed editor views.")
        except Exception as exc:
            messagebox.showerror("Refresh failed", str(exc))

    def check_references_dialog(self) -> None:
        try:
            if not self.base_dir:
                raise ValueError("No NBMS project is loaded")
            self.apply_note_tables_to_json()
            header, charts = self.read_editor_json()
            manifest = load_audio_manifest(self.base_dir, header)
            missing = find_missing_audio_refs(charts, manifest)
            if self.reference_tab:
                self.reference_tab.set_missing(missing)
            if missing:
                messagebox.showwarning("Reference check", f"Missing audio references: {len(missing)}")
            else:
                messagebox.showinfo("Reference check", "No missing audio references.")
        except Exception as exc:
            messagebox.showerror("Reference check failed", str(exc))

    def update_hashes(self, header: dict[str, Any], charts: list[tuple[Path, dict[str, Any]]]) -> None:
        if not self.base_dir:
            raise ValueError("No project directory")
        chart_by_name = {path.resolve(): chart for path, chart in charts}
        for chart_meta in header.get("charts", []):
            chart_path = (self.base_dir / chart_meta["file"]).resolve()
            chart = chart_by_name.get(chart_path)
            if chart is None:
                chart = load_json(chart_path)
            chart_meta["hash"] = canonical_json_hash(chart)
            chart_meta["hashAlgorithm"] = "sha256-canonical-json"

        audio_path = self.base_dir / header["audio"]["file"]
        header["audio"]["hash"] = sha256_file(audio_path)

    def prepare_header_for_save(self, header: dict[str, Any], charts: list[tuple[Path, dict[str, Any]]]) -> dict[str, Any]:
        self.update_hashes(header, charts)
        security = header.setdefault("security", {})
        if self.sign_on_save.get() and self.private_key:
            return sign_header(header, self.private_key)
        security["signed"] = False
        security.pop("signature", None)
        security.pop("publicKey", None)
        security.pop("publicKeyId", None)
        security.pop("signatureAlgorithm", None)
        return header

    def save_normal(self) -> None:
        try:
            if not self.header_path:
                raise ValueError("No NBMS project is loaded")
            self.apply_note_tables_to_json()
            header, charts = self.read_editor_json()
            for path, chart in charts:
                write_json(path, chart)
            saved_header = self.prepare_header_for_save(header, charts)
            write_json(self.header_path, saved_header)
            if self.header_tab:
                self.header_tab.set_json(saved_header)
            self.set_status(f"Saved {self.header_path}")
        except Exception as exc:
            messagebox.showerror("Save failed", str(exc))

    def save_encrypted_copy(self) -> None:
        try:
            if not self.header_path or not self.base_dir:
                raise ValueError("No NBMS project is loaded")
            output_dir = filedialog.askdirectory(title="Choose encrypted copy output directory")
            if not output_dir:
                return
            passphrase = simpledialog.askstring("Encryption", "Passphrase for encrypted audio:", show="*")
            if not passphrase:
                return

            self.save_normal()
            target_dir = Path(output_dir).resolve()
            if target_dir.exists() and any(target_dir.iterdir()):
                raise ValueError("Encrypted copy output directory must be empty")
            if target_dir.exists():
                target_dir.rmdir()
            shutil.copytree(self.base_dir, target_dir)
            target_header_path = target_dir / self.header_path.name
            target_header = load_json(target_header_path)
            target_audio = target_dir / target_header["audio"]["file"]
            encrypted_audio = target_audio.with_suffix(".tmp.nbma")
            encrypt_archive(target_audio, encrypted_audio, passphrase)
            shutil.move(str(encrypted_audio), str(target_audio))
            target_header["audio"]["hash"] = sha256_file(target_audio)
            target_header["security"] = {
                **target_header.get("security", {}),
                "encrypted": True,
                "editPolicy": "encrypted",
            }
            if self.sign_on_save.get() and self.private_key:
                target_header = sign_header(target_header, self.private_key)
            write_json(target_header_path, target_header)
            self.set_status(f"Saved encrypted copy to {target_dir}")
        except Exception as exc:
            messagebox.showerror("Encrypted save failed", str(exc))

    def convert_bms_dialog(self) -> None:
        try:
            bms_path = filedialog.askopenfilename(title="Open BMS", filetypes=[("BMS files", "*.bms *.bme *.bml"), ("All files", "*.*")])
            if not bms_path:
                return
            output_dir = filedialog.askdirectory(title="Choose conversion output directory")
            if not output_dir:
                return
            args = argparse.Namespace(
                input=Path(bms_path),
                output=Path(output_dir),
                chart_id="main",
                song_id=None,
                license="Converted from BMS; rights unspecified",
                sound_source="Converted BMS audio",
                no_flac_transcode=False,
                force=True,
            )
            result = convert_bms(args)
            if result != 0:
                raise RuntimeError(f"BMS conversion failed with exit code {result}")
            self.load_project(Path(output_dir) / "song.nbmh")
        except Exception as exc:
            messagebox.showerror("BMS conversion failed", str(exc))

    def package_dialog(self) -> None:
        try:
            if not self.header_path:
                raise ValueError("No NBMS project is loaded")
            self.save_normal()
            output = filedialog.asksaveasfilename(title="Save NBMS package", defaultextension=".nbmp", filetypes=[("NBMS package", "*.nbmp"), ("All files", "*.*")])
            if not output:
                return
            create_package(self.header_path, Path(output))
            self.set_status(f"Packaged {output}")
        except Exception as exc:
            messagebox.showerror("Package failed", str(exc))

    def generate_key_dialog(self) -> None:
        try:
            key_id = simpledialog.askstring("Generate key", "Key id:")
            if not key_id:
                return
            output_dir = filedialog.askdirectory(title="Choose key output directory")
            if not output_dir:
                return
            private_doc, public_doc = new_author_keypair(key_id)
            private_path = Path(output_dir) / f"{key_id}.private.json"
            public_path = Path(output_dir) / f"{key_id}.public.json"
            write_json(private_path, private_doc)
            write_json(public_path, public_doc)
            self.private_key = private_doc
            self.sign_on_save.set(True)
            self.set_status(f"Generated and loaded key {key_id}")
        except Exception as exc:
            messagebox.showerror("Key generation failed", str(exc))

    def load_key_dialog(self) -> None:
        try:
            path = filedialog.askopenfilename(title="Load private key", filetypes=[("NBMS private key", "*.json"), ("All files", "*.*")])
            if not path:
                return
            private_key = load_json(Path(path))
            if private_key.get("format") != "NBMS-AUTHOR-PRIVATE":
                raise ValueError("Selected file is not an NBMS private key")
            self.private_key = private_key
            self.sign_on_save.set(True)
            self.set_status(f"Loaded key {private_key.get('keyId')}")
        except Exception as exc:
            messagebox.showerror("Load key failed", str(exc))


def main() -> int:
    app = NbmsEditor()
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
