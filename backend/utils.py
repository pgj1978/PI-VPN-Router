"""
System command utilities for PiRouter
"""
import subprocess
from typing import List, Tuple
import logging

logger = logging.getLogger("uvicorn")


def run_command(cmd: List[str], use_sudo: bool = False) -> Tuple[bool, str]:
    """
    Execute a system command
    
    Args:
        cmd: Command and arguments as list
        use_sudo: Whether to prepend sudo to the command
        
    Returns:
        Tuple of (success: bool, output: str)
    """
    try:
        if use_sudo:
            cmd = ["sudo"] + cmd
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        return True, result.stdout
    except subprocess.CalledProcessError as e:
        return False, e.stderr
